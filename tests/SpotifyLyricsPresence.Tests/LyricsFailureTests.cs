using System.Net;
using SpotifyLyricsPresence.Core;
using static SpotifyLyricsPresence.Tests.T;

namespace SpotifyLyricsPresence.Tests;

public class LyricsFailureReasonTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task Http_error_is_reported_with_its_status()
    {
        var c = new LrcLibClient(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var r = await c.GetAsync(Track(), default);
        Assert.Equal(LyricsStatus.Error, r.Status);
        Assert.Contains("503", r.Detail);
    }

    [Fact]
    public async Task Timeout_is_reported_as_a_timeout()
    {
        var c = new LrcLibClient(new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) });
        var r = await c.GetAsync(Track(), default);
        Assert.Equal(LyricsStatus.Error, r.Status);
        Assert.Contains("timed out", r.Detail);
    }

    [Fact]
    public async Task Missing_lyrics_explain_why()
    {
        var c = new LrcLibClient(new HttpClient(new Handler(r =>
            r.RequestUri!.AbsolutePath == "/api/get" ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") })));
        var r = await c.GetAsync(Track(dur: 207), default);
        Assert.Equal(LyricsStatus.NotFound, r.Status);
        Assert.Contains("no entry", r.Detail);

        var wrongLength = new LrcLibClient(new HttpClient(new Handler(r =>
            r.RequestUri!.AbsolutePath == "/api/get" ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"trackName\":\"Song A\",\"artistName\":\"Artist\",\"duration\":300,\"syncedLyrics\":\"[00:01.00]x\"}]") })));
        var r2 = await wrongLength.GetAsync(Track(dur: 207), default);
        Assert.Equal(LyricsStatus.NotFound, r2.Status);
        Assert.Contains("207 s", r2.Detail);
    }
}

public class EngineLyricsRetryTests
{
    private sealed class FlakyLyrics : ILyricsProvider
    {
        public int Calls;
        public int FailFirst = 1;
        public Task<LyricsResult> GetAsync(TrackInfo track, CancellationToken ct) =>
            Task.FromResult(++Calls <= FailFirst ? LyricsResult.Failed("request to LRCLIB timed out after 10 s") : FakeLyrics.Synced());
    }

    private readonly FakeSource _src = new();
    private readonly FakeSink _sink = new();

    private (SharingEngine e, FlakyLyrics l) Make(int failFirst)
    {
        var l = new FlakyLyrics { FailFirst = failFirst };
        return (new SharingEngine(_src, l, _sink, () => "123456789012345678", S(4)), l);
    }

    [Fact]
    public async Task Failed_lookup_is_retried_and_the_lyric_then_appears_in_playing_card()
    {
        var (e, l) = Make(1);
        _src.Set(Track(), 12, true); e.Start(At(0));
        await e.TickAsync(At(0), default);
        Assert.Contains("timed out", e.State.LyricsStatusText);
        Assert.Contains("retrying", e.State.LyricsStatusText);
        Assert.Equal("Listening now", _sink.Sent[^1]!.State);

        await e.TickAsync(At(3.5), default);   // backoff (3 s) elapsed -> second attempt
        await e.TickAsync(At(8), default);
        Assert.Equal(2, l.Calls);
        Assert.Equal("one", _sink.Sent[^1]!.State);
    }

    [Fact]
    public async Task Gives_up_after_three_retries_and_says_so()
    {
        var (e, l) = Make(99);
        _src.Set(Track(), 12, true); e.Start(At(0));
        foreach (var t in new[] { 0, 4, 20, 60, 61 }) await e.TickAsync(At(t), default);
        Assert.Equal(4, l.Calls);               // initial + 3 retries
        Assert.Contains("Gave up", e.State.LyricsStatusText);
        Assert.Contains("Import LRC", e.State.LyricsStatusText);
    }

    [Fact]
    public async Task Track_change_resets_the_retry_counter()
    {
        var (e, l) = Make(99);
        _src.Set(Track("A"), 12, true); e.Start(At(0));
        foreach (var t in new[] { 0, 4, 20, 60 }) await e.TickAsync(At(t), default);
        _src.Set(Track("B"), 1, true);
        await e.TickAsync(At(61), default);
        Assert.Equal(5, l.Calls);
        Assert.DoesNotContain("Gave up", e.State.LyricsStatusText);
    }
}

public class AdvertisementTests
{
    [Fact]
    public async Task Item_without_artist_makes_no_request_and_says_why()
    {
        var calls = 0;
        var h = new HttpClient(new CountingHandler(() => calls++));
        var r = await new LrcLibClient(h).GetAsync(new TrackInfo("—", "", "", T.S(15)), default);
        Assert.Equal(LyricsStatus.NotFound, r.Status);
        Assert.Contains("no artist", r.Detail);
        Assert.Equal(0, calls);
    }

    private sealed class CountingHandler(Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { onCall(); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)); }
    }
}
