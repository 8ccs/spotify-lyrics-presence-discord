namespace SpotifyLyricsPresence.Core;

/// <summary>Identity of a recording as reported by the media session.</summary>
public sealed record TrackInfo(string Title, string Artist, string Album, TimeSpan Duration)
{
    /// <summary>Stable key used to decide whether two snapshots describe the same track.</summary>
    public string Key => $"{Title}\u001f{Artist}\u001f{Album}\u001f{(long)Duration.TotalSeconds}";
}

/// <summary>One observation of the media session at a moment in time.</summary>
/// <param name="IsAd">The media session looks like an advertisement (see <see cref="AdDetector"/>); Track is null then.</param>
public sealed record PlaybackSnapshot(TrackInfo? Track, TimeSpan Position, bool IsPlaying, DateTimeOffset SampledAt, bool IsAd = false);

public sealed record LyricLine(TimeSpan Time, string Text);

public enum LyricsStatus { Synced, Instrumental, PlainOnly, NotFound, Error }

/// <param name="Detail">Human-readable reason when lyrics are missing or a request failed.</param>
public sealed record LyricsResult(LyricsStatus Status, IReadOnlyList<LyricLine> Lines, string Source, string? Detail = null)
{
    public static LyricsResult NotFound { get; } = new(LyricsStatus.NotFound, Array.Empty<LyricLine>(), "none");
    public static LyricsResult Instrumental(string source) => new(LyricsStatus.Instrumental, Array.Empty<LyricLine>(), source);
    public static LyricsResult PlainOnly(string source, string? detail = null) => new(LyricsStatus.PlainOnly, Array.Empty<LyricLine>(), source, detail);
    public static LyricsResult Missing(string detail) => new(LyricsStatus.NotFound, Array.Empty<LyricLine>(), "none", detail);
    public static LyricsResult Failed(string detail) => new(LyricsStatus.Error, Array.Empty<LyricLine>(), "error", detail);
    public static LyricsResult Failure { get; } = new(LyricsStatus.Error, Array.Empty<LyricLine>(), "error");
}
