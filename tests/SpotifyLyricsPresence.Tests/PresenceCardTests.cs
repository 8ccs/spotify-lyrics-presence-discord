using SpotifyLyricsPresence.Core;
using static SpotifyLyricsPresence.Tests.T;

namespace SpotifyLyricsPresence.Tests;

public class ActivityJsonTests
{
    [Fact]
    public void Payload_is_a_playing_card_with_image_hover_and_second_timestamps()
    {
        var a = DiscordIpcClient.BuildActivity(new PresencePayload("Song — Artist", "مرحبا", 1_800_000_000, 1_800_000_200));
        Assert.Equal(0, (int)a["type"]!);
        Assert.Equal("Song — Artist", (string?)a["details"]);
        Assert.Equal("مرحبا", (string?)a["state"]);
        Assert.Equal("music_cover", (string?)a["assets"]!["large_image"]);
        Assert.Null(a["assets"]!["large_text"]);   // would render as a duplicate third line
        Assert.Null(a["name"]);                    // Rich Presence has no name field; Discord uses the application name
        Assert.Equal(1_800_000_000L, (long)a["timestamps"]!["start"]!);
        Assert.Equal(1_800_000_200L, (long)a["timestamps"]!["end"]!);
    }

    [Fact]
    public void No_timestamps_key_when_unknown() =>
        Assert.Null(DiscordIpcClient.BuildActivity(new PresencePayload("d", "s"))["timestamps"]);
}

public class ActivityNameTests
{
    [Fact]
    public void Name_is_included_in_every_update_when_set_and_absent_otherwise()
    {
        foreach (var lyric in new[] { "one", "two", "♪" })
            Assert.Equal("My Music", (string?)DiscordIpcClient.BuildActivity(new PresencePayload("d", lyric), "My Music")["name"]);
        Assert.Null(DiscordIpcClient.BuildActivity(new PresencePayload("d", "s"), "")["name"]);
        Assert.Null(DiscordIpcClient.BuildActivity(new PresencePayload("d", "s"), null)["name"]);
    }

    [Fact]
    public void Setting_defaults_to_off_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "slp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal("", new AppSettings().ActivityName);
            new AppSettings { ActivityName = "My Music" }.Save(path);
            Assert.Equal("My Music", AppSettings.Load(path).ActivityName);
        }
        finally { File.Delete(path); }
    }
}

public class ImageUrlTests
{
    private const string Url = "https://raw.githubusercontent.com/example/assets/main/x.gif";

    [Theory]
    [InlineData(Url, Url)]
    [InlineData("", "music_cover")]
    [InlineData(null, "music_cover")]
    [InlineData("http://insecure.example/x.gif", "music_cover")]
    [InlineData("not a url", "music_cover")]
    [InlineData("javascript:alert(1)", "music_cover")]
    public void Resolves_url_or_falls_back_to_art_asset(string? input, string expected) =>
        Assert.Equal(expected, DiscordIpcClient.ResolveImage(input));

    [Fact]
    public void Too_long_url_falls_back()
    {
        var longUrl = "https://example.com/" + new string('a', 250) + ".gif";
        Assert.Equal("music_cover", DiscordIpcClient.ResolveImage(longUrl));
    }

    [Fact]
    public void Every_update_carries_the_image_and_keeps_type_name_and_timestamps()
    {
        foreach (var lyric in new[] { "one", "two" })
        {
            var a = DiscordIpcClient.BuildActivity(new PresencePayload("d", lyric, 100, 200), "My Music", Url);
            Assert.Equal(Url, (string?)a["assets"]!["large_image"]);
            Assert.Equal(0, (int)a["type"]!);
            Assert.Equal("My Music", (string?)a["name"]);
            Assert.Equal(100L, (long)a["timestamps"]!["start"]!);
        }
    }

    [Fact]
    public void Setting_round_trips_and_defaults_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), "slp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal("", new AppSettings().ImageUrl);
            new AppSettings { ImageUrl = Url }.Save(path);
            Assert.Equal(Url, AppSettings.Load(path).ImageUrl);
        }
        finally { File.Delete(path); }
    }
}

public class TimerAndFallbackTests
{
    private readonly FakeSource _src = new();
    private readonly FakeLyrics _lyr = new();
    private readonly FakeSink _sink = new();
    private readonly SharingEngine _e;

    public TimerAndFallbackTests()
    {
        _lyr.ByTitle["Song A"] = FakeLyrics.Synced();
        _lyr.ByTitle["Song B"] = FakeLyrics.Synced("[00:01.00]bee");
        _lyr.ByTitle["Inst"] = LyricsResult.Instrumental("fake");
        _e = new SharingEngine(_src, _lyr, _sink, () => "123456789012345678", S(4));
    }

    private Task Tick(double at) => _e.TickAsync(At(at), default);

    [Fact]
    public async Task Timestamps_come_from_real_position_and_duration()
    {
        _src.Set(Track(dur: 100), 12, true); _e.Start(At(0)); await Tick(0);
        var p = _sink.Sent[^1]!;
        Assert.Equal(At(0).ToUnixTimeSeconds() - 12, p.StartUnix);
        Assert.Equal(p.StartUnix + 100, p.EndUnix);
    }

    [Fact]
    public async Task Lyric_change_does_not_move_timer_but_seek_and_resume_do()
    {
        _src.Set(Track(), 8, true); _e.Start(At(0)); await Tick(0);
        var first = _sink.Sent[^1]!;
        _src.Set(Track(), 12.1, true); await Tick(4.1);            // "one" appears; playback continued normally
        var second = _sink.Sent[^1]!;
        Assert.NotEqual(first.State, second.State);
        Assert.Equal(first.StartUnix, second.StartUnix);
        Assert.Equal(first.EndUnix, second.EndUnix);

        _src.Set(Track(), 60, true); await Tick(8.2);              // seek forward
        Assert.NotEqual(second.StartUnix, _sink.Sent[^1]!.StartUnix);

        _src.Set(Track(), 63, false); await Tick(12.3);            // pause clears the card
        Assert.Null(_sink.Sent[^1]);
        _src.Set(Track(), 63, true); await Tick(16.4);             // resume: timer matches position 63
        Assert.Equal(At(16.4).ToUnixTimeSeconds() - 63, _sink.Sent[^1]!.StartUnix);
    }

    [Fact]
    public async Task Missing_shows_fallback_and_instrumental_only_when_confirmed()
    {
        _src.Set(Track("Unknown"), 5, true); _e.Start(At(0)); await Tick(0);
        Assert.Equal("Listening now", _sink.Sent[^1]!.State);
        Assert.StartsWith("No synced lyrics found", _e.State.LyricsStatusText);
        _src.Set(Track("Inst"), 5, true); await Tick(4);
        Assert.Equal("Instrumental", _sink.Sent[^1]!.State);
    }

    [Fact]
    public async Task Old_songs_lyric_never_appears_under_new_song()
    {
        _src.Set(Track(), 32, true); _e.Start(At(0)); await Tick(0);   // "three" from Song A
        _src.Set(Track("Song B", 60), 0.2, true); await Tick(4);
        Assert.All(_sink.Sent.Where(p => p!.Details.StartsWith("Song B")), p => Assert.NotEqual("three", p!.State));
        Assert.Equal("Song B — Artist", _sink.Sent[^1]!.Details);
    }
}

public class LongTextTests
{
    [Fact]
    public void Long_english_line_breaks_on_a_word()
    {
        var s = DiscordText.Field(string.Join(" ", Enumerable.Repeat("wonderful", 30)));
        Assert.True(s.Length <= 128);
        Assert.EndsWith("wonderful…", s);
    }
}
