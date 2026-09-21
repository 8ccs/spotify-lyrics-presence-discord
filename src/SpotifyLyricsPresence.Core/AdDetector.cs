namespace SpotifyLyricsPresence.Core;

/// <summary>
/// Spotify advertisement detection from Windows media-session data.
/// Observed on a free account: during ads (and the moment before one) Spotify turns BOTH the Next and Previous
/// transport controls off and reports no album; songs report an album and have working skip controls.
/// Duration, missing lyrics and the advertiser's name are deliberately NOT used: songs can be short or lyric-less.
/// Limitation: a genuine track with no album that is alone in the queue (both skip buttons disabled) would look like an ad.
/// </summary>
public static class AdDetector
{
    public static bool IsAdvertisement(string? album, bool nextEnabled, bool previousEnabled) =>
        !nextEnabled && !previousEnabled && string.IsNullOrWhiteSpace(album);
}
