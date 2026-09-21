namespace SpotifyLyricsPresence.Core;

public interface IPlaybackSource
{
    /// <summary>Current Spotify session state, or null when Spotify has no media session.</summary>
    Task<PlaybackSnapshot?> GetAsync(CancellationToken ct);
}

/// <summary>What the UI shows; a copy is produced on every tick.</summary>
public sealed record EngineState(
    TrackInfo? Track, bool IsPlaying, TimeSpan Position, string LyricText, string LyricsStatusText,
    bool Sharing, string ConnectionText, bool IsAd = false);

/// <summary>
/// Drives everything: samples playback, keeps a local clock, loads lyrics per track, picks the
/// current line and pushes it to Discord no faster than the throttle allows.
/// Time is injected via <c>now</c> so the whole pipeline is testable with synthetic data.
/// </summary>
public sealed class SharingEngine
{
    public const string GapGlyph = "♪";
    private static readonly TimeSpan ReconnectEvery = TimeSpan.FromSeconds(5);

    private readonly IPlaybackSource _source;
    private readonly ILyricsProvider _lyrics;
    private readonly IPresenceSink _sink;
    private readonly Func<string> _appId;
    private readonly PlaybackClock _clock = new();
    private readonly PresenceThrottle _throttle;

    private CancellationTokenSource? _loadCts;
    private Task<LyricsResult>? _loadTask;
    private string? _loadKey;
    private LyricsResult? _result;
    private LyricsTimeline _timeline = new(Array.Empty<LyricLine>());
    private DateTimeOffset _nextConnect = DateTimeOffset.MinValue;
    private PresencePayload? _desired;
    private DateTimeOffset? _anchor;          // wall-clock moment the track "started": start = now - position
    private string? _anchorKey;
    private static readonly TimeSpan AnchorTolerance = TimeSpan.FromSeconds(2);
    public const string FallbackText = "Listening now";
    public const string AdDetails = "Ad";
    public const string AdState = "Wait a sec…";
    private const int AdExitTicks = 2;        // consecutive non-ad samples needed before leaving ad mode
    private bool _adMode;
    private int _nonAdStreak;
    private static readonly TimeSpan[] RetryBackoff = { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };
    private int _attempts;
    private DateTimeOffset? _retryAt;

    /// <summary>Optional diagnostics sink (lyrics lookups and their outcome).</summary>
    public Action<string>? Log { get; set; }

    public SharingEngine(IPlaybackSource source, ILyricsProvider lyrics, IPresenceSink sink, Func<string> appId, TimeSpan? minUpdateInterval = null)
    {
        _source = source; _lyrics = lyrics; _sink = sink; _appId = appId;
        _throttle = new PresenceThrottle(minUpdateInterval ?? TimeSpan.FromSeconds(4));
    }

    public bool Sharing { get; private set; }
    public TimeSpan Offset { get; set; }
    public EngineState State { get; private set; } = new(null, false, TimeSpan.Zero, "", "Waiting for Spotify", false, "Not sharing");

    public void Start(DateTimeOffset now) { Sharing = true; _nextConnect = DateTimeOffset.MinValue; _throttle.Invalidate(_desired); }

    /// <summary>Stop sharing and clear the card immediately (bypasses update spacing).</summary>
    public async Task StopAsync(DateTimeOffset now, CancellationToken ct)
    {
        Sharing = false;
        if (_sink.IsConnected) await _sink.SetAsync(null, ct);
        _throttle.MarkSent(now, null);
    }

    /// <summary>Force the lyrics for the current track to be looked up again (e.g. after importing an LRC file).</summary>
    public void ReloadLyrics() { _loadKey = null; _result = null; }

    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        PlaybackSnapshot? snap = null;
        try { snap = await _source.GetAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }

        if (snap is { IsAd: true }) { _adMode = true; _nonAdStreak = 0; }
        else if (snap is null) { _adMode = false; _nonAdStreak = 0; }
        else if (_adMode && ++_nonAdStreak >= AdExitTicks) { _adMode = false; }

        if (snap is not null) _clock.Update(snap with { SampledAt = now });
        if (snap is null && _clock.Track is not null) _clock.Update(new PlaybackSnapshot(null, TimeSpan.Zero, false, now));
        var track = _clock.Track;

        if (_adMode || track is null) { CancelLoad(); _result = null; _loadKey = null; _timeline = new LyricsTimeline(Array.Empty<LyricLine>()); } // ads: no lookups, no stale lyric
        else if (_loadKey != track.Key) { _attempts = 0; _retryAt = null; BeginLoad(track); }
        else if (_result is { Status: LyricsStatus.Error } && _retryAt is { } due && now >= due)
        {
            _attempts++; _retryAt = null; BeginLoad(track); // a failed request is retried, not kept for the whole song
        }

        if (!_adMode && _loadTask is { IsCompleted: true } t && _loadKey == track?.Key && _result is null)
        {
            _result = t.IsCompletedSuccessfully ? t.Result
                : LyricsResult.Failed("lookup crashed: " + (t.Exception?.GetBaseException().Message ?? "cancelled"));
            _timeline = new LyricsTimeline(_result.Lines);
            if (_result.Status == LyricsStatus.Error && _attempts < RetryBackoff.Length) _retryAt = now + RetryBackoff[_attempts];
            Log?.Invoke($"lyrics for '{track!.Title}' / '{track.Artist}' ({(int)track.Duration.TotalSeconds}s, album '{track.Album}'): {_result.Status}, {_result.Lines.Count} lines, {_result.Source}{(_result.Detail is null ? "" : ", " + _result.Detail)}{(_retryAt is null ? "" : $", retry {_attempts + 1}/{RetryBackoff.Length} in {(_retryAt.Value - now).TotalSeconds:0}s")}");
        }

        var pos = _clock.PositionAt(now);
        var lyricText = "";
        var statusText = track is null ? "Waiting for Spotify" : _result is null ? (_attempts > 0 ? $"Loading lyrics… (retry {_attempts}/{RetryBackoff.Length})" : "Loading lyrics…") : Describe(_result, _attempts >= RetryBackoff.Length);
        if (_result is { Status: LyricsStatus.Synced })
        {
            var text = _timeline.TextAt(pos, Offset);
            lyricText = string.IsNullOrWhiteSpace(text) ? GapGlyph : text;
        }

        // Timer anchor: only re-derived on track change, resume or a seek; a lyric change never touches it.
        if (track is null || !_clock.IsPlaying) { _anchor = null; _anchorKey = null; }
        else
        {
            var candidate = now - pos;
            if (_anchor is null || _anchorKey != track.Key || (candidate - _anchor.Value).Duration() > AnchorTolerance)
            { _anchor = candidate; _anchorKey = track.Key; }
        }

        if (track is null || !_clock.IsPlaying || _anchor is null) _desired = null;
        else
        {
            var state = _result switch
            {
                { Status: LyricsStatus.Synced } => lyricText,
                { Status: LyricsStatus.Instrumental } => "Instrumental",
                _ => FallbackText, // loading, missing, unsynced or error: technical detail stays in the desktop app
            };
            var details = DiscordText.Details(track);
            var start = _anchor.Value.ToUnixTimeSeconds();
            long? end = track.Duration > TimeSpan.Zero ? start + (long)Math.Round(track.Duration.TotalSeconds) : null;
            _desired = new PresencePayload(details, DiscordText.Field(state), start, end);
        }

        if (_adMode)
        {
            _anchor = null; _anchorKey = null; lyricText = "";
            statusText = "Advertisement detected — lyric lookups paused";
            _desired = _clock.IsPlaying ? new PresencePayload(AdDetails, AdState) : null; // no timestamps: timer hidden
        }

        var conn = "Not sharing";
        if (Sharing)
        {
            if (!_sink.IsConnected && now >= _nextConnect)
            {
                _nextConnect = now + ReconnectEvery;
                if (await _sink.ConnectAsync(_appId(), ct)) _throttle.Invalidate(_desired);
            }
            if (_sink.IsConnected)
            {
                _throttle.Desire(_desired);
                if (_throttle.TryTake(now, out var payload) && !await _sink.SetAsync(payload, ct))
                    _nextConnect = now; // lost the pipe: reconnect on the next tick and resend
            }
            conn = _sink.StatusText;
        }

        State = new EngineState(_adMode ? null : track, _clock.IsPlaying, pos, lyricText, statusText, Sharing, conn, _adMode);
    }

    private static string Describe(LyricsResult r, bool gaveUp) => r.Status switch
    {
        LyricsStatus.Synced => $"Synced lyrics ({r.Source})",
        LyricsStatus.Instrumental => "Instrumental — no lyrics",
        LyricsStatus.PlainOnly => $"{r.Detail ?? "Only unsynced lyrics exist"} — not shown. Use Import LRC… for timed lyrics",
        LyricsStatus.NotFound => $"No synced lyrics found: {r.Detail ?? "no match"}. Use Import LRC… to add your own",
        _ => $"Lyrics request failed: {r.Detail ?? "unknown error"}" + (gaveUp ? ". Gave up after retries; use Import LRC… or change track to retry" : " — retrying"),
    };

    private void BeginLoad(TrackInfo track)
    {
        CancelLoad();
        _result = null; _timeline = new LyricsTimeline(Array.Empty<LyricLine>());
        _loadKey = track.Key;
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        _loadTask = _lyrics.GetAsync(track, ct);
    }

    private void CancelLoad() { _loadCts?.Cancel(); _loadCts = null; _loadTask = null; }
}
