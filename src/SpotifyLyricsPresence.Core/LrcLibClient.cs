using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SpotifyLyricsPresence.Core;

public interface ILyricsProvider
{
    Task<LyricsResult> GetAsync(TrackInfo track, CancellationToken ct);
}

/// <summary>Adapter for the documented LRCLIB HTTP API (https://lrclib.net/docs).</summary>
public sealed class LrcLibClient : ILyricsProvider
{
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(3);
    private readonly HttpClient _http;

    public LrcLibClient(HttpClient http)
    {
        _http = http;
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri("https://lrclib.net");
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyLyricsPresence/1.0 (https://github.com/8ccs/spotify-lyrics-presence)");
    }

    private sealed class Dto
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }

    public async Task<LyricsResult> GetAsync(TrackInfo track, CancellationToken ct)
    {
        // Ads and placeholders arrive without a usable artist; LRCLIB answers such queries with HTTP 400.
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            return LyricsResult.Missing("Spotify reported no artist for this item (advertisement?), so no lookup was made");
        try
        {
            // 1) exact signature lookup (LRCLIB matches duration within a couple of seconds)
            var exact = await GetExactAsync(track, ct);
            if (exact is not null && Convert(exact, track) is { } r1 && r1.Status != LyricsStatus.NotFound) return r1;

            // 2) search, then choose the best candidate ourselves using metadata + duration
            var queries = new List<string> { $"track_name={Uri.EscapeDataString(TrackMatcher.CoreTitle(track.Title))}&artist_name={Uri.EscapeDataString(track.Artist)}" };
            queries.Add($"q={Uri.EscapeDataString((TrackMatcher.CoreTitle(track.Title) + " " + track.Artist).Trim())}");

            Dto? best = null; var bestScore = 0.0; var seen = 0;
            foreach (var q in queries)
            {
                var list = await SearchAsync(q, ct);
                foreach (var c in list)
                {
                    seen++;
                    if (c.Instrumental is false && string.IsNullOrWhiteSpace(c.SyncedLyrics) && string.IsNullOrWhiteSpace(c.PlainLyrics)) continue;
                    var s = TrackMatcher.Score(track, c.TrackName ?? "", c.ArtistName ?? "", c.Duration ?? 0, DurationTolerance);
                    if (s < 0) continue;
                    if (!string.IsNullOrWhiteSpace(c.SyncedLyrics)) s += 2; // prefer timestamped
                    if (best is null || s > bestScore) { best = c; bestScore = s; }
                }
                if (best is not null) break;
            }
            return best is null
                ? LyricsResult.Missing(seen == 0
                    ? "LRCLIB has no entry for this title and artist"
                    : $"LRCLIB has {seen} entries for this title, but none match the artist and length ({(int)Math.Round(track.Duration.TotalSeconds)} s, +/-{(int)DurationTolerance.TotalSeconds} s)")
                : Convert(best, track);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return LyricsResult.Failed($"request to LRCLIB timed out after {(int)_http.Timeout.TotalSeconds} s"); }
        catch (HttpRequestException ex)
        {
            return LyricsResult.Failed(ex.StatusCode is { } sc ? $"LRCLIB answered HTTP {(int)sc}" : "network error: " + ex.Message);
        }
        catch (System.Text.Json.JsonException) { return LyricsResult.Failed("LRCLIB returned unreadable data"); }
    }

    private async Task<Dto?> GetExactAsync(TrackInfo t, CancellationToken ct)
    {
        var url = $"/api/get?track_name={Uri.EscapeDataString(t.Title)}&artist_name={Uri.EscapeDataString(t.Artist)}" +
                  $"&album_name={Uri.EscapeDataString(t.Album)}&duration={(int)Math.Round(t.Duration.TotalSeconds)}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Dto>(cancellationToken: ct);
    }

    private async Task<List<Dto>> SearchAsync(string query, CancellationToken ct)
    {
        using var resp = await _http.GetAsync("/api/search?" + query, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new();
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<Dto>>(cancellationToken: ct) ?? new();
    }

    private static LyricsResult Convert(Dto d, TrackInfo want)
    {
        // Reject an exact-lookup hit whose length disagrees with the playing recording.
        if (want.Duration > TimeSpan.Zero && d.Duration is > 0 &&
            Math.Abs(d.Duration.Value - want.Duration.TotalSeconds) > DurationTolerance.TotalSeconds)
            return LyricsResult.Missing("closest LRCLIB entry has a different length than this recording");
        if (d.Instrumental) return LyricsResult.Instrumental("lrclib");
        var lines = LrcParser.Parse(d.SyncedLyrics);
        if (lines.Count > 0) return new LyricsResult(LyricsStatus.Synced, lines, "lrclib");
        return string.IsNullOrWhiteSpace(d.PlainLyrics) ? LyricsResult.Missing("LRCLIB entry has no lyrics") : LyricsResult.PlainOnly("lrclib", "LRCLIB only has unsynced (untimed) lyrics for this recording");
    }
}
