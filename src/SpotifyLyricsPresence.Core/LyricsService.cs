using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyLyricsPresence.Core;

/// <summary>
/// Resolution order: user-imported LRC file, then local disk cache, then the remote provider.
/// Only successful (synced / instrumental) remote results are written to disk.
/// </summary>
public sealed class LyricsService : ILyricsProvider
{
    private sealed record CacheEntry(string Status, string? Lrc);

    private readonly ILyricsProvider _remote;
    private readonly string _dir;

    public LyricsService(ILyricsProvider remote, string cacheDirectory)
    {
        _remote = remote; _dir = cacheDirectory;
    }

    public static string KeyFor(TrackInfo t)
    {
        var s = $"{TrackMatcher.Normalize(t.Title)}|{TrackMatcher.Normalize(t.Artist)}|{(int)Math.Round(t.Duration.TotalSeconds)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();
    }

    private string ManualPath(TrackInfo t) => Path.Combine(_dir, $"manual-{KeyFor(t)}.lrc");
    private string CachePath(TrackInfo t) => Path.Combine(_dir, $"{KeyFor(t)}.json");

    public bool HasManual(TrackInfo t) => File.Exists(ManualPath(t));

    /// <summary>Attach a local LRC file to a track. Returns the parsed line count (0 = file rejected).</summary>
    public int ImportManual(TrackInfo t, string lrcText)
    {
        var lines = LrcParser.Parse(lrcText);
        if (lines.Count == 0) return 0;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ManualPath(t), lrcText, Encoding.UTF8);
        return lines.Count;
    }

    public void RemoveManual(TrackInfo t) { if (File.Exists(ManualPath(t))) File.Delete(ManualPath(t)); }

    public async Task<LyricsResult> GetAsync(TrackInfo track, CancellationToken ct)
    {
        if (File.Exists(ManualPath(track)))
        {
            var lines = LrcParser.Parse(await File.ReadAllTextAsync(ManualPath(track), ct));
            if (lines.Count > 0) return new LyricsResult(LyricsStatus.Synced, lines, "local file");
        }

        try
        {
            if (File.Exists(CachePath(track)))
            {
                var e = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(CachePath(track), ct));
                if (e?.Status == nameof(LyricsStatus.Instrumental)) return LyricsResult.Instrumental("cache");
                var lines = LrcParser.Parse(e?.Lrc);
                if (lines.Count > 0) return new LyricsResult(LyricsStatus.Synced, lines, "cache");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* corrupt cache entry: refetch */ }

        var r = await _remote.GetAsync(track, ct);
        if (r.Status is LyricsStatus.Synced or LyricsStatus.Instrumental)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var lrc = r.Status == LyricsStatus.Synced ? Serialize(r.Lines) : null;
                await File.WriteAllTextAsync(CachePath(track), JsonSerializer.Serialize(new CacheEntry(r.Status.ToString(), lrc)), ct);
            }
            catch (IOException) { /* cache is best-effort */ }
        }
        return r;
    }

    private static string Serialize(IReadOnlyList<LyricLine> lines) =>
        string.Join("\n", lines.Select(l => $"[{(int)l.Time.TotalMinutes:00}:{l.Time.Seconds:00}.{l.Time.Milliseconds / 10:00}]{l.Text}"));
}
