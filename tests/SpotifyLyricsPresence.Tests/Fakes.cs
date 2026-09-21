using SpotifyLyricsPresence.Core;

namespace SpotifyLyricsPresence.Tests;

internal static class T
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public static DateTimeOffset At(double s) => Start + TimeSpan.FromSeconds(s);
    public static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    public static TrackInfo Track(string title = "Song A", double dur = 100) => new(title, "Artist", "Album", S(dur));

    /// <summary>Synthetic lyrics: line N starts at 10*N seconds, N = 1..5.</summary>
    public const string Lrc = "[ti:Synthetic]\n[00:10.00]one\n[00:20.00]two\n[00:30.00]three\n[00:40.00]\n[00:50.00]five";
}

internal sealed class FakeSource : IPlaybackSource
{
    public PlaybackSnapshot? Next;
    public Task<PlaybackSnapshot?> GetAsync(CancellationToken ct) => Task.FromResult(Next);
    public void SetAd(bool playing = true) => Next = new PlaybackSnapshot(null, TimeSpan.Zero, playing, DateTimeOffset.MinValue, IsAd: true);
    public void Set(TrackInfo? t, double pos, bool playing) => Next = new PlaybackSnapshot(t, T.S(pos), playing, DateTimeOffset.MinValue);
}

internal sealed class FakeLyrics : ILyricsProvider
{
    public Dictionary<string, LyricsResult> ByTitle = new();
    public int Calls;
    public Task<LyricsResult> GetAsync(TrackInfo track, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(ByTitle.TryGetValue(track.Title, out var r) ? r : LyricsResult.NotFound);
    }
    public static LyricsResult Synced(string lrc = T.Lrc) => new(LyricsStatus.Synced, LrcParser.Parse(lrc), "fake");
}

internal sealed class FakeSink : IPresenceSink
{
    public bool IsConnected { get; set; }
    public string StatusText => IsConnected ? "Connected" : "Down";
    public bool ConnectWorks = true;
    public int Connects;
    public List<PresencePayload?> Sent = new();
    public Task<bool> ConnectAsync(string id, CancellationToken ct) { Connects++; IsConnected = ConnectWorks; return Task.FromResult(ConnectWorks); }
    public Task<bool> SetAsync(PresencePayload? p, CancellationToken ct)
    {
        if (!IsConnected) return Task.FromResult(false);
        Sent.Add(p); return Task.FromResult(true);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
