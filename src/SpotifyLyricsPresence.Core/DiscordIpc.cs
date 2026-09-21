using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpotifyLyricsPresence.Core;

public interface IPresenceSink : IAsyncDisposable
{
    bool IsConnected { get; }
    string StatusText { get; }
    Task<bool> ConnectAsync(string applicationId, CancellationToken ct);
    /// <summary>Set the activity, or clear it when payload is null. Returns false if the connection is lost.</summary>
    Task<bool> SetAsync(PresencePayload? payload, CancellationToken ct);
}

/// <summary>Discord local-IPC frame: int32 opcode, int32 length (little endian), UTF-8 JSON.</summary>
public static class DiscordFrame
{
    public const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;

    public static async Task WriteAsync(Stream s, int opcode, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), body.Length);
        body.CopyTo(buf, 8);
        await s.WriteAsync(buf, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<(int Opcode, string Json)> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[8];
        await s.ReadExactlyAsync(head, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0, 4));
        var len = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4, 4));
        if (len < 0 || len > 1 << 20) throw new InvalidDataException("Bad Discord frame length " + len);
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return (op, Encoding.UTF8.GetString(body));
    }
}

/// <summary>Rich Presence over Discord's documented local IPC (named pipe discord-ipc-0..9).</summary>
public sealed class DiscordIpcClient : IPresenceSink
{
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private volatile bool _connected;

    public bool IsConnected => _connected;
    public string StatusText { get; private set; } = "Not connected";
    /// <summary>Optional activity-name override, included in every SET_ACTIVITY (including after reconnects).</summary>
    public volatile string? ActivityName;
    /// <summary>Optional public HTTPS image link for assets.large_image; falls back to <see cref="LargeImageKey"/>.</summary>
    public volatile string? ImageUrl;
    /// <summary>Optional diagnostics hook: receives every SET_ACTIVITY sent and every reply from Discord.</summary>
    public Action<string>? Trace { get; set; }

    public async Task<bool> ConnectAsync(string applicationId, CancellationToken ct)
    {
        await CloseAsync();
        if (string.IsNullOrWhiteSpace(applicationId) || !applicationId.All(char.IsDigit))
        {
            StatusText = "Set a numeric Discord application ID";
            return false;
        }

        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(1500);
                await pipe.ConnectAsync(to.Token);
                await DiscordFrame.WriteAsync(pipe, DiscordFrame.Handshake,
                    new JsonObject { ["v"] = 1, ["client_id"] = applicationId }.ToJsonString(), to.Token);
                var (op, json) = await DiscordFrame.ReadAsync(pipe, to.Token);
                var node = JsonNode.Parse(json);
                if (op == DiscordFrame.Frame && node?["evt"]?.GetValue<string>() == "READY")
                {
                    _pipe = pipe; _connected = true; StatusText = "Connected to Discord";
                    _readerCts = new CancellationTokenSource();
                    _ = Task.Run(() => ReadLoopAsync(pipe, _readerCts.Token));
                    return true;
                }
                StatusText = "Discord rejected the application ID" + (node?["data"]?["message"] is { } m ? $": {m}" : "");
                await pipe.DisposeAsync();
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { await pipe.DisposeAsync(); throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidDataException)
            {
                await pipe.DisposeAsync();
            }
        }
        StatusText = "Discord is not running (no IPC pipe found)";
        return false;
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, json) = await DiscordFrame.ReadAsync(pipe, ct);
                if (op != DiscordFrame.Ping) Trace?.Invoke("IN  " + json);
                if (op == DiscordFrame.Ping) await DiscordFrame.WriteAsync(pipe, DiscordFrame.Pong, json, ct);
                else if (op == DiscordFrame.Close) break;
            }
        }
        catch { /* pipe closed or cancelled */ }
        if (ReferenceEquals(_pipe, pipe)) { _connected = false; StatusText = "Discord connection lost"; }
    }

    /// <summary>Art-asset key uploaded under the application's Rich Presence -> Art Assets.</summary>
    public const string LargeImageKey = "music_cover";
    /// <summary>Discord RPC allows Playing (0), Listening (2), Watching (3), Competing (5); we use Playing.</summary>
    public const int ActivityTypePlaying = 0;

    /// <summary>Activity object for SET_ACTIVITY. Type, image and timestamps are part of every update.</summary>
    /// <summary>The large_image value: a valid https URL (max 256 chars, Discord's limit) or the art-asset key.</summary>
    public static string ResolveImage(string? imageUrl)
    {
        var u = imageUrl?.Trim();
        return u is { Length: > 0 and <= 256 } && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? u : LargeImageKey;
    }

    public static JsonObject BuildActivity(PresencePayload payload, string? activityName = null, string? imageUrl = null)
    {
        var a = new JsonObject
        {
            ["type"] = ActivityTypePlaying, // sent in every update, so reconnects keep it
            ["details"] = payload.Details,
            ["state"] = payload.State,
            // No large_text: it would repeat the song as an extra text line on the card.
            ["assets"] = new JsonObject { ["large_image"] = ResolveImage(imageUrl) },
            ["instance"] = false,
        };
        if (!string.IsNullOrWhiteSpace(activityName)) a["name"] = activityName.Trim();
        if (payload.StartUnix is { } start)
        {
            var ts = new JsonObject { ["start"] = start };
            if (payload.EndUnix is { } end) ts["end"] = end;
            a["timestamps"] = ts;
        }
        return a;
    }

    public async Task<bool> SetAsync(PresencePayload? payload, CancellationToken ct)
    {
        if (!_connected || _pipe is null) return false;
        var args = new JsonObject { ["pid"] = Environment.ProcessId };
        if (payload is not null) args["activity"] = BuildActivity(payload, ActivityName, ImageUrl);
        var cmd = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = Guid.NewGuid().ToString() };
        try
        {
            Trace?.Invoke("OUT " + cmd.ToJsonString());
            await DiscordFrame.WriteAsync(_pipe, DiscordFrame.Frame, cmd.ToJsonString(), ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _connected = false; StatusText = "Discord connection lost";
            return false;
        }
    }

    private async Task CloseAsync()
    {
        _connected = false;
        _readerCts?.Cancel();
        var p = _pipe; _pipe = null;
        if (p is not null) await p.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connected && _pipe is not null)
        {
            using var cts = new CancellationTokenSource(1000);
            try { await SetAsync(null, cts.Token); } catch { }
        }
        await CloseAsync();
    }
}
