using System.Net;
using SpotifyLyricsPresence.Core;
using static SpotifyLyricsPresence.Tests.T;

namespace SpotifyLyricsPresence.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_lines_sorted_and_skips_metadata()
    {
        var l = LrcParser.Parse("[ar:x]\n[00:20.5]b\n[00:10.00]a");
        Assert.Equal(new[] { "a", "b" }, l.Select(x => x.Text));
        Assert.Equal(S(20.5), l[1].Time);
    }

    [Fact]
    public void Multiple_stamps_share_text_and_word_tags_are_stripped()
    {
        var l = LrcParser.Parse("[00:05.00][00:15.00]<00:05.10>hel<00:05.40>lo");
        Assert.Equal(2, l.Count);
        Assert.All(l, x => Assert.Equal("hello", x.Text));
    }

    [Fact]
    public void Offset_tag_is_applied_and_garbage_is_ignored()
    {
        var l = LrcParser.Parse("[offset:1000]\n[00:10.00]a\nnot lyrics");
        Assert.Single(l);
        Assert.Equal(S(9), l[0].Time);
        Assert.Empty(LrcParser.Parse("plain text only"));
        Assert.Empty(LrcParser.Parse(null));
    }

    [Fact]
    public void Arabic_text_is_preserved()
    {
        var l = LrcParser.Parse("[00:01.00]مرحبا بالعالم");
        Assert.Equal("مرحبا بالعالم", l[0].Text);
    }
}

public class TimelineTests
{
    private static LyricsTimeline Tl() => new(LrcParser.Parse(Lrc));

    [Theory]
    [InlineData(0, null)]
    [InlineData(9.9, null)]
    [InlineData(10, "one")]
    [InlineData(19.99, "one")]
    [InlineData(20, "two")]
    [InlineData(45, "")]
    [InlineData(999, "five")]
    public void Picks_active_line(double pos, string? expected) => Assert.Equal(expected, Tl().TextAt(S(pos), TimeSpan.Zero));

    [Fact]
    public void Positive_offset_delays_and_negative_advances()
    {
        Assert.Null(Tl().TextAt(S(11), S(2)));            // "one" now appears at 12s
        Assert.Equal("one", Tl().TextAt(S(12), S(2)));
        Assert.Equal("two", Tl().TextAt(S(18), S(-2)));   // "two" now appears at 18s
    }

    [Fact]
    public void Next_change_accounts_for_offset()
    {
        Assert.Equal(S(20), Tl().NextChangeAt(S(12), TimeSpan.Zero));
        Assert.Equal(S(21.5), Tl().NextChangeAt(S(12), S(1.5)));
        Assert.Null(Tl().NextChangeAt(S(60), TimeSpan.Zero));
    }
}

public class PlaybackClockTests
{
    private static PlaybackSnapshot Snap(TrackInfo? t, double pos, bool play, double at) => new(t, S(pos), play, T.At(at));

    [Fact]
    public void Position_advances_only_while_playing()
    {
        var c = new PlaybackClock();
        c.Update(Snap(Track(), 5, true, 0));
        Assert.Equal(S(8), c.PositionAt(T.At(3)));
        c.Update(Snap(Track(), 8, false, 3));
        Assert.Equal(S(8), c.PositionAt(T.At(30)));
    }

    [Fact]
    public void Classifies_events()
    {
        var c = new PlaybackClock();
        Assert.Equal(PlaybackEvent.TrackChanged, c.Update(Snap(Track(), 0, true, 0)));
        Assert.Equal(PlaybackEvent.None, c.Update(Snap(Track(), 5.2, true, 5)));          // normal drift
        Assert.Equal(PlaybackEvent.Seeked, c.Update(Snap(Track(), 60, true, 6)));          // forward seek
        Assert.Equal(PlaybackEvent.Seeked, c.Update(Snap(Track(), 20, true, 7)));          // backward seek
        Assert.Equal(PlaybackEvent.Paused, c.Update(Snap(Track(), 21, false, 8)));
        Assert.Equal(PlaybackEvent.Resumed, c.Update(Snap(Track(), 21, true, 9)));
        Assert.Equal(PlaybackEvent.TrackChanged, c.Update(Snap(Track("Song B"), 0, true, 10)));
    }

    [Fact]
    public void Repeat_of_same_track_is_detected()
    {
        var c = new PlaybackClock();
        c.Update(Snap(Track(dur: 100), 97, true, 0));
        Assert.Equal(PlaybackEvent.Restarted, c.Update(Snap(Track(dur: 100), 0.5, true, 4)));
    }

    [Fact]
    public void Position_is_clamped_to_duration()
    {
        var c = new PlaybackClock();
        c.Update(Snap(Track(dur: 100), 99, true, 0));
        Assert.Equal(S(100), c.PositionAt(T.At(50)));
    }
}

public class PresenceThrottleTests
{
    private static PresencePayload P(string s) => new("d", s);

    [Fact]
    public void First_update_is_immediate_then_spaced()
    {
        var t = new PresenceThrottle(S(4));
        t.Desire(P("a"));
        Assert.True(t.TryTake(At(0), out var p)); Assert.Equal(P("a"), p);
        t.Desire(P("b"));
        Assert.False(t.TryTake(At(3.9), out _));
        Assert.True(t.TryTake(At(4), out p)); Assert.Equal(P("b"), p);
    }

    [Fact]
    public void Outdated_queued_lines_are_discarded()
    {
        var t = new PresenceThrottle(S(4));
        t.Desire(P("a")); t.TryTake(At(0), out _);
        t.Desire(P("b")); t.Desire(P("c")); t.Desire(P("d"));
        Assert.True(t.TryTake(At(4), out var p));
        Assert.Equal(P("d"), p);
        Assert.False(t.TryTake(At(20), out _)); // b and c never sent
    }

    [Fact]
    public void Returning_to_already_sent_state_cancels_pending()
    {
        var t = new PresenceThrottle(S(4));
        t.Desire(P("a")); t.TryTake(At(0), out _);
        t.Desire(P("b")); t.Desire(P("a"));
        Assert.False(t.HasPending);
    }

    [Fact]
    public void Clear_is_a_normal_update_and_reconnect_resends()
    {
        var t = new PresenceThrottle(S(4));
        t.Desire(P("a")); t.TryTake(At(0), out _);
        t.Desire(null);
        Assert.True(t.TryTake(At(5), out var p)); Assert.Null(p);
        t.Invalidate(P("a"));
        Assert.True(t.TryTake(At(10), out p)); Assert.Equal(P("a"), p);
    }
}

public class SharingEngineTests
{
    private readonly FakeSource _src = new();
    private readonly FakeLyrics _lyr = new();
    private readonly FakeSink _sink = new();
    private readonly SharingEngine _e;

    public SharingEngineTests()
    {
        _lyr.ByTitle["Song A"] = FakeLyrics.Synced();
        _lyr.ByTitle["Song B"] = FakeLyrics.Synced("[00:01.00]bee");
        _e = new SharingEngine(_src, _lyr, _sink, () => "123456789012345678", S(4));
    }

    private Task Tick(double at) => _e.TickAsync(At(at), default);

    [Fact]
    public async Task Sends_current_line_with_song_in_details()
    {
        _src.Set(Track(), 12, true);
        _e.Start(At(0));
        await Tick(0);
        var p = Assert.Single(_sink.Sent);
        Assert.Equal("Song A — Artist", p!.Details);
        Assert.Equal("one", p.State);
    }

    [Fact]
    public async Task Local_clock_advances_lines_between_samples_and_respects_spacing()
    {
        _src.Set(Track(), 8, true);
        _e.Start(At(0));
        await Tick(0);                         // pos 8 -> before first line: gap glyph
        Assert.Equal(DiscordText.Field(SharingEngine.GapGlyph), _sink.Sent[^1]!.State);
        _src.Set(Track(), 12, true); await Tick(1);   // line "one" is due but <4s since last send
        Assert.Single(_sink.Sent);
        _src.Set(Track(), 16, true); await Tick(4);   // allowed now
        Assert.Equal("one", _sink.Sent[^1]!.State);
    }

    [Fact]
    public async Task Seek_shows_line_for_new_position_and_drops_stale_one()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        _src.Set(Track(), 12.25, true); await Tick(0.25);
        _src.Set(Track(), 32, true); await Tick(1);      // seek during throttle window
        _src.Set(Track(), 33, true); await Tick(4.5);
        Assert.Equal(new[] { "one", "three" }, _sink.Sent.Select(p => p!.State));
    }

    [Fact]
    public async Task Track_change_reloads_lyrics_and_updates_card()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        _src.Set(Track("Song B", 60), 2, true); await Tick(4);
        Assert.Equal(2, _lyr.Calls);
        Assert.Equal("Song B — Artist", _sink.Sent[^1]!.Details);
        Assert.Equal("bee", _sink.Sent[^1]!.State);
    }

    [Fact]
    public async Task Pause_clears_and_resume_restores()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        _src.Set(Track(), 13, false); await Tick(5);
        Assert.Null(_sink.Sent[^1]);
        _src.Set(Track(), 13, true); await Tick(10);
        Assert.Equal("one", _sink.Sent[^1]!.State);
    }

    [Fact]
    public async Task Missing_lyrics_are_reported_honestly_not_invented()
    {
        _src.Set(Track("Unknown"), 12, true); _e.Start(At(0)); await Tick(0);
        Assert.Equal("Listening now", _sink.Sent[^1]!.State);
        Assert.StartsWith("No synced lyrics found", _e.State.LyricsStatusText); // technical text stays in the app
        Assert.Equal("", _e.State.LyricText);
    }

    [Fact]
    public async Task Instrumental_is_labelled()
    {
        _lyr.ByTitle["Inst"] = LyricsResult.Instrumental("fake");
        _src.Set(Track("Inst"), 5, true); _e.Start(At(0)); await Tick(0);
        Assert.Contains("Instrumental", _sink.Sent[^1]!.State);
    }

    [Fact]
    public async Task Offset_setting_shifts_displayed_line()
    {
        _e.Offset = S(5);
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        Assert.Equal(DiscordText.Field(SharingEngine.GapGlyph), _sink.Sent[^1]!.State); // "one" now due at 15s
    }

    [Fact]
    public async Task Stop_clears_immediately_and_nothing_is_sent_afterwards()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        await _e.StopAsync(At(0.5), default);
        Assert.Null(_sink.Sent[^1]);
        var n = _sink.Sent.Count;
        _src.Set(Track(), 32, true); await Tick(30);
        Assert.Equal(n, _sink.Sent.Count);
    }

    [Fact]
    public async Task Reconnects_after_discord_restart_and_resends_current_line()
    {
        _src.Set(Track(), 12, true); _e.Start(At(0)); await Tick(0);
        _sink.IsConnected = false; _sink.ConnectWorks = false;
        _src.Set(Track(), 22, true); await Tick(6);
        Assert.Equal("Down", _e.State.ConnectionText);
        _sink.ConnectWorks = true;
        _src.Set(Track(), 26, true); await Tick(12);
        Assert.Equal("two", _sink.Sent[^1]!.State);
        Assert.True(_sink.Connects >= 2);
    }

    [Fact]
    public async Task Preview_works_without_sharing_and_never_touches_discord()
    {
        _src.Set(Track(), 12, true); await Tick(0);
        Assert.Equal("one", _e.State.LyricText);
        Assert.Empty(_sink.Sent); Assert.Equal(0, _sink.Connects);
    }
}

public class DiscordTextTests
{
    [Fact]
    public void Enforces_limits_without_splitting_characters()
    {
        Assert.Equal(128, DiscordText.Field(new string('a', 500)).Length);
        var emoji = DiscordText.Field(string.Concat(Enumerable.Repeat("😀", 200)));
        Assert.True(emoji.Length <= 128);
        Assert.False(char.IsHighSurrogate(emoji[^2]));
        Assert.True(DiscordText.Field("x").Length >= 2);
        Assert.True(DiscordText.Field("").Length >= 2);
        Assert.DoesNotContain('\n', DiscordText.Field("a\nb"));
    }

    [Fact]
    public void Arabic_long_line_truncates_cleanly()
    {
        var s = DiscordText.Field(string.Concat(Enumerable.Repeat("مَرْحَبًا ", 60)));
        Assert.True(s.Length <= 128);
        Assert.EndsWith("…", s);
    }
}

public class MatcherTests
{
    [Fact]
    public void Arabic_diacritics_and_variants_fold()
    {
        Assert.Equal(TrackMatcher.Normalize("أُغْنِيَةٌ"), TrackMatcher.Normalize("اغنيه"));
        Assert.Equal(TrackMatcher.Normalize("مَرْحَبًا"), TrackMatcher.Normalize("مرحبا"));
        Assert.Equal(TrackMatcher.Normalize("آمال"), TrackMatcher.Normalize("امال"));
    }

    [Fact]
    public void Core_title_drops_decorations()
    {
        Assert.Equal("Song", TrackMatcher.CoreTitle("Song (feat. X) [Remastered]"));
        Assert.Equal("Song", TrackMatcher.CoreTitle("Song - 2011 Remaster"));
    }

    [Fact]
    public void Duration_and_metadata_drive_selection()
    {
        var want = new TrackInfo("Song", "Artist", "Alb", S(200));
        var tol = LrcLibClient.DurationTolerance;
        Assert.True(TrackMatcher.Score(want, "Song", "Artist", 201, tol) > 0);
        Assert.True(TrackMatcher.Score(want, "Song", "Artist", 230, tol) < 0);         // wrong recording length
        Assert.True(TrackMatcher.Score(want, "Other", "Artist", 200, tol) < 0);        // wrong title
        Assert.True(TrackMatcher.Score(want, "Song", "Nobody Else", 200, tol) < 0);    // wrong artist
        Assert.True(TrackMatcher.Score(want, "Song", "Artist", 200, tol) > TrackMatcher.Score(want, "Song", "Artist", 202, tol));
    }
}

public class LrcLibClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Urls.Add(r.RequestUri!.PathAndQuery); return Task.FromResult(f(r)); }
    }
    private static HttpResponseMessage Json(string j, HttpStatusCode c = HttpStatusCode.OK) =>
        new(c) { Content = new StringContent(j, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Exact_hit_returns_synced_lyrics_and_sends_duration()
    {
        var h = new Handler(_ => Json("{\"trackName\":\"Song A\",\"artistName\":\"Artist\",\"duration\":100,\"instrumental\":false,\"syncedLyrics\":\"[00:01.00]hi\"}"));
        var r = await new LrcLibClient(new HttpClient(h)).GetAsync(Track(), default);
        Assert.Equal(LyricsStatus.Synced, r.Status);
        Assert.Contains("duration=100", h.Urls[0]);
    }

    [Fact]
    public async Task Instrumental_flag_is_honoured()
    {
        var h = new Handler(_ => Json("{\"trackName\":\"Song A\",\"duration\":100,\"instrumental\":true}"));
        Assert.Equal(LyricsStatus.Instrumental, (await new LrcLibClient(new HttpClient(h)).GetAsync(Track(), default)).Status);
    }

    [Fact]
    public async Task Falls_back_to_search_and_rejects_wrong_duration()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath == "/api/get" ? Json("{}", HttpStatusCode.NotFound)
            : Json("[{\"trackName\":\"Song A\",\"artistName\":\"Artist\",\"duration\":180,\"syncedLyrics\":\"[00:01.00]wrong\"}," +
                   "{\"trackName\":\"Song A\",\"artistName\":\"Artist\",\"duration\":101,\"syncedLyrics\":\"[00:01.00]right\"}]"));
        var r = await new LrcLibClient(new HttpClient(h)).GetAsync(Track(), default);
        Assert.Equal("right", r.Lines[0].Text);
    }

    [Fact]
    public async Task Nothing_found_and_network_errors_are_distinguished()
    {
        var nf = new Handler(_ => Json("[]", HttpStatusCode.OK));
        var gone = new Handler(r => r.RequestUri!.AbsolutePath == "/api/get" ? Json("{}", HttpStatusCode.NotFound) : Json("[]"));
        Assert.Equal(LyricsStatus.NotFound, (await new LrcLibClient(new HttpClient(gone)).GetAsync(Track(), default)).Status);
        var err = new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(LyricsStatus.Error, (await new LrcLibClient(new HttpClient(err)).GetAsync(Track(), default)).Status);
    }

    [Fact]
    public async Task Plain_only_lyrics_are_not_treated_as_synced()
    {
        var h = new Handler(_ => Json("{\"trackName\":\"Song A\",\"duration\":100,\"plainLyrics\":\"no times\"}"));
        Assert.Equal(LyricsStatus.PlainOnly, (await new LrcLibClient(new HttpClient(h)).GetAsync(Track(), default)).Status);
    }
}

public class LyricsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slp-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public async Task Caches_successes_and_prefers_manual_import()
    {
        var fake = new FakeLyrics { ByTitle = { ["Song A"] = FakeLyrics.Synced() } };
        var svc = new LyricsService(fake, _dir);
        await svc.GetAsync(Track(), default);
        var second = await svc.GetAsync(Track(), default);
        Assert.Equal(1, fake.Calls);
        Assert.Equal("cache", second.Source);

        Assert.Equal(0, svc.ImportManual(Track(), "no timestamps"));
        Assert.Equal(1, svc.ImportManual(Track(), "[00:01.00]mine"));
        var manual = await svc.GetAsync(Track(), default);
        Assert.Equal("mine", manual.Lines[0].Text);
        Assert.Equal("local file", manual.Source);
    }

    [Fact]
    public async Task Misses_and_errors_are_not_cached()
    {
        var fake = new FakeLyrics();
        var svc = new LyricsService(fake, _dir);
        await svc.GetAsync(Track("Nope"), default);
        await svc.GetAsync(Track("Nope"), default);
        Assert.Equal(2, fake.Calls);
    }
}

public class DiscordFrameTests
{
    [Fact]
    public async Task Frame_round_trips_with_little_endian_header()
    {
        var ms = new MemoryStream();
        await DiscordFrame.WriteAsync(ms, DiscordFrame.Frame, "{\"a\":\"مرحبا\"}", default);
        var bytes = ms.ToArray();
        Assert.Equal(1, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
        ms.Position = 0;
        var (op, json) = await DiscordFrame.ReadAsync(ms, default);
        Assert.Equal(1, op); Assert.Equal("{\"a\":\"مرحبا\"}", json);
    }
}

