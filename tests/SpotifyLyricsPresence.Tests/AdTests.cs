using SpotifyLyricsPresence.Core;
using static SpotifyLyricsPresence.Tests.T;

namespace SpotifyLyricsPresence.Tests;

public class AdDetectorTests
{
    [Theory]
    [InlineData("", false, false, true)]                 // observed ad: no album, both skip controls off
    [InlineData(null, false, false, true)]
    [InlineData("Currents", true, true, false)]          // normal song
    [InlineData("", true, true, false)]                  // empty album alone is not an ad
    [InlineData("", true, false, false)]                 // first track of a queue without an album
    [InlineData("Currents", false, false, false)]        // only track in the queue but has an album
    public void Needs_disabled_skip_controls_and_no_album(string? album, bool next, bool prev, bool expected) =>
        Assert.Equal(expected, AdDetector.IsAdvertisement(album, next, prev));
}

public class AdEngineTests
{
    private readonly FakeSource _src = new();
    private readonly FakeSink _sink = new();
    private readonly FakeLyrics _lyr = new();
    private readonly SharingEngine _e;

    public AdEngineTests()
    {
        _lyr.ByTitle["Song A"] = FakeLyrics.Synced();
        _lyr.ByTitle["Song B"] = FakeLyrics.Synced("[00:01.00]bee");
        _e = new SharingEngine(_src, _lyr, _sink, () => "123456789012345678", S(4));
    }

    private Task Tick(double at) => _e.TickAsync(At(at), default);

    [Fact]
    public async Task Song_ad_song_shows_ad_card_then_restores_lyric_and_timer()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        Assert.Equal("one", _sink.Sent[^1]!.State);

        _src.SetAd(); await Tick(4);
        var ad = _sink.Sent[^1]!;
        Assert.Equal("Ad", ad.Details);
        Assert.Equal("Wait a sec…", ad.State);
        Assert.Null(ad.StartUnix); Assert.Null(ad.EndUnix);            // timer hidden
        Assert.True(_e.State.IsAd);
        Assert.Equal("", _e.State.LyricText);                          // previous lyric cleared

        _src.Set(Track(), 35, true);                                   // music resumes at a new position
        await Tick(8); await Tick(8.25); await Tick(8.5);
        var back = _sink.Sent[^1]!;
        Assert.Equal("Song A — Artist", back.Details);
        Assert.Equal("three", back.State);
        Assert.Equal(At(8.5).ToUnixTimeSeconds() - 35, back.StartUnix);
    }

    [Fact]
    public async Task Consecutive_ads_never_restore_the_previous_song()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        var songCards = _sink.Sent.Count;
        _src.SetAd(); await Tick(4);
        _src.SetAd(); await Tick(8);            // second ad (with empty-title transition, still IsAd)
        _src.SetAd(); await Tick(12);
        // between ads a single non-ad-looking sample must not flip back yet
        _src.Set(Track("Song B", 60), 0.1, true); await Tick(12.25);
        _src.SetAd(); await Tick(16);
        Assert.All(_sink.Sent.Skip(songCards), p => Assert.Equal("Ad", p!.Details));
        _src.Set(Track("Song B", 60), 2, true);
        await Tick(20); await Tick(20.25); await Tick(20.5);
        Assert.Equal("Song B — Artist", _sink.Sent[^1]!.Details);
    }

    [Fact]
    public async Task Lookups_are_suspended_and_delayed_responses_cannot_overwrite_the_ad()
    {
        var late = new TaskCompletionSource<LyricsResult>();
        var provider = new DeferredLyrics(late.Task);
        var e = new SharingEngine(_src, provider, _sink, () => "123456789012345678", S(4));
        _src.Set(Track(), 12, true); e.Start(At(0)); await e.TickAsync(At(0), default);   // lookup pending
        Assert.Equal(1, provider.Calls);

        _src.SetAd(); await e.TickAsync(At(4), default);
        late.SetResult(FakeLyrics.Synced());                                             // response arrives during the ad
        await e.TickAsync(At(8), default); await e.TickAsync(At(12), default);
        Assert.Equal("Ad", _sink.Sent[^1]!.Details);
        Assert.Equal("Wait a sec…", _sink.Sent[^1]!.State);
        Assert.Equal(1, provider.Calls);                                                 // no new lookups during the ad
    }

    [Fact]
    public async Task Ordinary_song_without_lyrics_stays_in_normal_mode()
    {
        _src.Set(Track("Unknown", 12), 5, true); _e.Start(At(0)); await Tick(0);
        var p = _sink.Sent[^1]!;
        Assert.Equal("Unknown — Artist", p.Details);
        Assert.Equal("Listening now", p.State);
        Assert.NotNull(p.StartUnix);
        Assert.False(_e.State.IsAd);
    }

    [Fact]
    public async Task Pause_during_ad_clears_the_card()
    {
        _src.SetAd(); _e.Start(At(0)); await Tick(0);
        Assert.Equal("Ad", _sink.Sent[^1]!.Details);
        _src.SetAd(playing: false); await Tick(5);
        Assert.Null(_sink.Sent[^1]);
    }

    private sealed class DeferredLyrics(Task<LyricsResult> result) : ILyricsProvider
    {
        public int Calls;
        public Task<LyricsResult> GetAsync(TrackInfo track, CancellationToken ct) { Calls++; return result; }
    }
}
