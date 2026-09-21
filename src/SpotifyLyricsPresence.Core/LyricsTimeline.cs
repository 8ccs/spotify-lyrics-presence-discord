namespace SpotifyLyricsPresence.Core;

/// <summary>Maps a playback position to the active lyric line. Pure and stateless.</summary>
public sealed class LyricsTimeline
{
    private readonly IReadOnlyList<LyricLine> _lines;
    public LyricsTimeline(IReadOnlyList<LyricLine> lines) => _lines = lines;
    public int Count => _lines.Count;

    /// <summary>
    /// Index of the line active at <paramref name="position"/> with a user offset applied
    /// (positive offset = lyrics appear later; negative = earlier). -1 before the first line.
    /// </summary>
    public int IndexAt(TimeSpan position, TimeSpan offset)
    {
        var t = position - offset;
        int lo = 0, hi = _lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_lines[mid].Time <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Text of the active line; empty for blank (instrumental gap) lines; null before the first line.</summary>
    public string? TextAt(TimeSpan position, TimeSpan offset)
    {
        var i = IndexAt(position, offset);
        return i < 0 ? null : _lines[i].Text;
    }

    /// <summary>Playback position at which the next line becomes active, or null after the last line.</summary>
    public TimeSpan? NextChangeAt(TimeSpan position, TimeSpan offset)
    {
        var next = IndexAt(position, offset) + 1;
        return next < _lines.Count ? _lines[next].Time + offset : null;
    }
}
