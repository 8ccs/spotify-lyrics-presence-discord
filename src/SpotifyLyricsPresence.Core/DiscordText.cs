using System.Globalization;
using System.Text;

namespace SpotifyLyricsPresence.Core;

/// <summary>Formats text for Rich Presence fields (details/state accept 2..128 characters).</summary>
public static class DiscordText
{
    public const int MaxLength = 128;
    private const char InvisiblePad = '⠀'; // braille blank: satisfies the 2-char minimum without adding visible text

    public static string Field(string? text)
    {
        var t = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (t.Length > MaxLength) t = Truncate(t, MaxLength);
        while (t.Length < 2) t += InvisiblePad;
        return t;
    }

    private static string Truncate(string t, int max)
    {
        // Cut on text-element boundaries so surrogate pairs / combining marks (Arabic diacritics) are never split.
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(t);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            if (sb.Length + el.Length > max - 1) break;
            sb.Append(el);
        }
        var cut = sb.ToString();
        var space = cut.LastIndexOf(' ');
        if (space > cut.Length * 0.6) cut = cut[..space]; // prefer ending on a whole word
        return cut.TrimEnd(' ', ',', '.', '،') + "…";
    }

    public static string Details(TrackInfo t) =>
        Field(string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Title} — {t.Artist}");
}
