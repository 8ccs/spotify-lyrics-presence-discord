using System.Globalization;
using System.Text.RegularExpressions;

namespace SpotifyLyricsPresence.Core;

/// <summary>Parses timestamped LRC text ("[mm:ss.xx] line"). Lines are returned sorted by time.</summary>
public static class LrcParser
{
    private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex Meta = new(@"^\[([A-Za-z#]+):(.*)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        var offset = TimeSpan.Zero;
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            var stamps = Stamp.Matches(line);
            if (stamps.Count == 0 || stamps[0].Index != 0)
            {
                var meta = Meta.Match(line);
                if (meta.Success && meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                    offset = TimeSpan.FromMilliseconds(ms); // LRC: positive offset makes lyrics appear earlier
                continue;
            }

            // Consecutive leading stamps share the text that follows the last one.
            var end = 0;
            var leading = new List<Match>();
            foreach (Match m in stamps)
            {
                if (m.Index != end) break;
                leading.Add(m); end = m.Index + m.Length;
            }
            var text = WordTag.Replace(line[end..], "").Trim();
            foreach (var m in leading)
            {
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var frac = 0.0;
                if (m.Groups[3].Success)
                    frac = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / Math.Pow(10, m.Groups[3].Length);
                result.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec + frac), text));
            }
        }

        if (offset != TimeSpan.Zero)
            for (var i = 0; i < result.Count; i++)
            {
                var t = result[i].Time - offset;
                result[i] = result[i] with { Time = t < TimeSpan.Zero ? TimeSpan.Zero : t };
            }

        return result.OrderBy(l => l.Time).ToList(); // OrderBy is stable
    }
}
