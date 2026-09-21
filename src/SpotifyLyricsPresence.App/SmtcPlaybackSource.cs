using SpotifyLyricsPresence.Core;
using Windows.Media.Control;

namespace SpotifyLyricsPresence.App;

/// <summary>Reads Spotify's Windows media session (GlobalSystemMediaTransportControlsSessionManager).</summary>
public sealed class SmtcPlaybackSource : IPlaybackSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public static bool IsSpotify(GlobalSystemMediaTransportControlsSession s) =>
        s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

    public async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync() =>
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    public async Task<PlaybackSnapshot?> GetAsync(CancellationToken ct)
    {
        var mgr = await ManagerAsync();
        var session = mgr.GetSessions().FirstOrDefault(IsSpotify);
        if (session is null) return null;

        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var info = session.GetPlaybackInfo();
        var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var now = DateTimeOffset.UtcNow;

        // Position is valid as of LastUpdatedTime; extrapolate while playing.
        var pos = timeline.Position;
        if (playing && timeline.LastUpdatedTime > DateTimeOffset.MinValue.AddYears(1))
        {
            var age = now - timeline.LastUpdatedTime;
            if (age > TimeSpan.Zero && age < TimeSpan.FromHours(1)) pos += age;
        }

        // Ads: Spotify disables both skip controls and reports no album (see AdDetector).
        var controls = info.Controls;
        if (AdDetector.IsAdvertisement(props.AlbumTitle, controls.IsNextEnabled, controls.IsPreviousEnabled))
            return new PlaybackSnapshot(null, TimeSpan.Zero, playing, now, IsAd: true);

        var title = props.Title ?? "";
        if (title.Length == 0 || (props.Artist.Length == 0 && title is "Advertisement" or "Spotify"))
            return new PlaybackSnapshot(null, TimeSpan.Zero, playing, now);

        var duration = timeline.EndTime - timeline.StartTime;
        var track = new TrackInfo(title, props.Artist ?? "", props.AlbumTitle ?? "", duration);
        return new PlaybackSnapshot(track, pos, playing, now);
    }

    /// <summary>Human-readable dump of everything this installation exposes, for --probe.</summary>
    public async Task<string> ProbeAsync()
    {
        var mgr = await ManagerAsync();
        var sb = new System.Text.StringBuilder();
        var sessions = mgr.GetSessions();
        sb.AppendLine($"Media sessions: {sessions.Count}");
        foreach (var s in sessions)
        {
            sb.AppendLine($"- {s.SourceAppUserModelId}{(IsSpotify(s) ? "  <-- Spotify" : "")}");
            var p = await s.TryGetMediaPropertiesAsync();
            var t = s.GetTimelineProperties();
            var i = s.GetPlaybackInfo();
            sb.AppendLine($"    title={p.Title} | artist={p.Artist} | album={p.AlbumTitle}");
            sb.AppendLine($"    status={i.PlaybackStatus} start={t.StartTime} end={t.EndTime} position={t.Position} lastUpdated={t.LastUpdatedTime:O}");
        }
        return sb.ToString();
    }
}
