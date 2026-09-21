using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpotifyLyricsPresence.Core;

/// <summary>Metadata normalisation and candidate scoring, including Arabic-aware folding.</summary>
public static class TrackMatcher
{
    private static readonly Regex Bracketed = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex DashSuffix = new(@"\s+-\s+(?:.*(?:remaster|version|edit|mix|live|mono|stereo|deluxe).*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Lower-cases, strips diacritics/tashkeel/tatweel, folds Arabic letter variants, drops punctuation.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            if (ch == 'ـ') continue; // tatweel
            var c = ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => ch,
            };
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Title without "(feat. …)", "[Remastered]" or "- 2011 Remaster" style decorations.</summary>
    public static string CoreTitle(string? title)
    {
        var t = Bracketed.Replace(title ?? "", " ");
        t = DashSuffix.Replace(t, "");
        return t.Trim();
    }

    /// <summary>Score for a candidate recording; higher is better, negative means reject.</summary>
    public static double Score(TrackInfo want, string candTitle, string candArtist, double candDurationSeconds, TimeSpan tolerance)
    {
        var dur = want.Duration.TotalSeconds;
        var diff = dur > 0 && candDurationSeconds > 0 ? Math.Abs(dur - candDurationSeconds) : 0;
        if (dur > 0 && candDurationSeconds > 0 && diff > tolerance.TotalSeconds) return -1;

        var wt = Normalize(CoreTitle(want.Title));
        var ct = Normalize(CoreTitle(candTitle));
        if (wt.Length == 0 || ct.Length == 0) return -1;
        double score = 0;
        if (wt == ct) score += 10;
        else if (wt.Contains(ct) || ct.Contains(wt)) score += 4;
        else return -1;

        var wa = Normalize(want.Artist);
        var ca = Normalize(candArtist);
        if (wa.Length > 0 && ca.Length > 0)
        {
            if (wa == ca) score += 6;
            else if (wa.Contains(ca) || ca.Contains(wa)) score += 4;
            else if (wa.Split(' ').Intersect(ca.Split(' ')).Any()) score += 1;
            else return -1;
        }
        return score - diff * 0.5;
    }
}
