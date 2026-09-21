namespace SpotifyLyricsPresence.Core;

public enum PlaybackEvent { None, TrackChanged, Seeked, Restarted, Paused, Resumed }

/// <summary>
/// Tracks the playback position locally between coarse media-session samples and
/// classifies what changed when a new sample arrives.
/// </summary>
public sealed class PlaybackClock
{
    private readonly TimeSpan _seekTolerance;
    private PlaybackSnapshot? _last;

    public PlaybackClock(TimeSpan? seekTolerance = null) => _seekTolerance = seekTolerance ?? TimeSpan.FromSeconds(1.5);

    public TrackInfo? Track => _last?.Track;
    public bool IsPlaying => _last is { IsPlaying: true };

    /// <summary>Estimated position at <paramref name="now"/>, extrapolated from the last sample while playing.</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        if (_last is null) return TimeSpan.Zero;
        var pos = _last.Position;
        if (_last.IsPlaying) pos += now - _last.SampledAt;
        var dur = _last.Track?.Duration ?? TimeSpan.Zero;
        if (dur > TimeSpan.Zero && pos > dur) pos = dur;
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }

    public PlaybackEvent Update(PlaybackSnapshot snap)
    {
        var prev = _last;
        var expected = prev is null ? TimeSpan.Zero : PositionAt(snap.SampledAt);
        _last = snap;
        if (prev is null) return snap.Track is null ? PlaybackEvent.None : PlaybackEvent.TrackChanged;
        if (prev.Track?.Key != snap.Track?.Key) return PlaybackEvent.TrackChanged;
        if (snap.Track is null) return PlaybackEvent.None;

        var drift = snap.Position - expected;
        if (Math.Abs(drift.TotalSeconds) > _seekTolerance.TotalSeconds)
        {
            // Same track wrapping from near its end back to ~0 is a repeat, not a user seek.
            var wasNearEnd = snap.Track.Duration > TimeSpan.Zero && expected > snap.Track.Duration - TimeSpan.FromSeconds(5);
            return drift < TimeSpan.Zero && snap.Position < TimeSpan.FromSeconds(3) && wasNearEnd
                ? PlaybackEvent.Restarted : PlaybackEvent.Seeked;
        }
        if (prev.IsPlaying && !snap.IsPlaying) return PlaybackEvent.Paused;
        if (!prev.IsPlaying && snap.IsPlaying) return PlaybackEvent.Resumed;
        return PlaybackEvent.None;
    }
}
