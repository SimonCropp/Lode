/// <summary>
/// A line Find in Files shows: one that matches, with where in it each match is, or one either
/// side of such a line, for context.
/// </summary>
/// <param name="Number">Its number in the file, from 1.</param>
/// <param name="Text">As it is in the file, without its line ending, cut down around the first
/// match when it is long, a line of minified script say, with "…" where it was cut.</param>
/// <param name="Column">Where the first match starts in the whole line, from 1: where an editor
/// opening the file there puts the cursor.</param>
/// <param name="FirstMatch">Which of the file's matches, counted in order from 0, is the first on
/// it, and <paramref name="LastMatch"/> the last, however many a long line cut down shows: a
/// match over several lines is on each of them, and is counted once when they are all taken out
/// of the replace. -1 for a line of context.</param>
sealed record ResultLine(int Number, string Text, IReadOnlyList<(int Start, int Length)> Matches, int Column, int FirstMatch = -1, int LastMatch = -1)
{
    public bool IsContext => Matches.Count == 0;
}

/// <summary>
/// A file Find in Files found what it looks for in: how many times, and the lines to show.
/// </summary>
/// <param name="MatchCount">How many matches Replace would replace: those on lines taken out of
/// the results aren't counted.</param>
/// <param name="LinesLeftOut">Too much had been found already for its lines to be kept.</param>
sealed record FileResult(string Path, int MatchCount, IReadOnlyList<ResultLine> Lines, bool LinesLeftOut = false)
{
    /// <summary>
    /// The lines taken out of the results, as they were found, whose matches Replace leaves as
    /// they are.
    /// </summary>
    public IReadOnlyList<ResultLine> Skipped { get; init; } = [];

    // The longest line kept whole. Past it, a window round the first match is kept.
    const int longestLine = 300;

    // How much of a long line is kept before its first match.
    const int beforeMatch = 80;

    /// <summary>
    /// The lines the matches are on, in order, with <paramref name="context"/> lines either
    /// side of each. A match over several lines puts each of them in.
    /// </summary>
    public static FileResult From(string path, CharSpan text, IReadOnlyList<(int Start, int Length)> matches, int context)
    {
        // The start of each line a match is on, where the matches on it are, by line, and which
        // of the matches they are.
        var lineStarts = new List<int> {0};
        var onLine = new SortedDictionary<int, List<(int Start, int Length)>>();
        var which = new Dictionary<int, (int First, int Last)>();
        var scanned = 0;

        static int LineOf(CharSpan text, List<int> lineStarts, ref int scanned, int position)
        {
            while (scanned < position)
            {
                var next = text[scanned..position].IndexOf('\n');
                if (next < 0)
                {
                    scanned = position;
                    break;
                }

                scanned += next + 1;
                lineStarts.Add(scanned);
            }

            var index = lineStarts.BinarySearch(position);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return index;
        }

        for (var match = 0; match < matches.Count; match++)
        {
            var (start, length) = matches[match];
            var first = LineOf(text, lineStarts, ref scanned, start);
            var last = LineOf(text, lineStarts, ref scanned, Math.Max(start, start + length - 1));
            for (var line = first; line <= last; line++)
            {
                var lineStart = lineStarts[line];
                var from = Math.Max(start, lineStart);
                var to = start + length;
                if (line < last)
                {
                    to = lineStarts[line + 1];
                }

                if (!onLine.TryGetValue(line, out var list))
                {
                    list = [];
                    onLine[line] = list;
                }

                list.Add((from - lineStart, to - from));
                which[line] = which.TryGetValue(line, out var known) ? (known.First, match) : (match, match);
            }
        }

        // Every line start up to the end, for the context after the last match.
        LineOf(text, lineStarts, ref scanned, text.Length);
        var lineCount = lineStarts.Count;

        // Ending with a line break ends the last line, rather than starting one with nothing in.
        if (lineCount > 1 &&
            lineStarts[^1] == text.Length)
        {
            lineCount--;
        }
        var shown = new SortedSet<int>();
        foreach (var line in onLine.Keys)
        {
            for (var near = Math.Max(0, line - context); near <= Math.Min(lineCount - 1, line + context); near++)
            {
                shown.Add(near);
            }
        }

        var lines = new List<ResultLine>(shown.Count);
        foreach (var line in shown)
        {
            var start = lineStarts[line];
            var end = text.Length;
            if (line + 1 < lineCount)
            {
                end = lineStarts[line + 1];
            }

            var whole = text[start..end].TrimEnd("\r\n");
            onLine.TryGetValue(line, out var ranges);
            var shownLine = Line(line + 1, whole, ranges ?? []);
            if (which.TryGetValue(line, out var on))
            {
                shownLine = shownLine with
                {
                    FirstMatch = on.First,
                    LastMatch = on.Last
                };
            }

            lines.Add(shownLine);
        }

        return new(path, matches.Count, lines);
    }

    // A line as shown: whole, or the part round its first match, the matches moved to match.
    static ResultLine Line(int number, CharSpan whole, List<(int Start, int Length)> matches)
    {
        var column = 1;
        if (matches.Count > 0)
        {
            column = matches[0].Start + 1;
        }

        var wholeLength = whole.Length;
        if (wholeLength <= longestLine)
        {
            return new(number, whole.ToString(), matches.Select(_ => (_.Start, Math.Min(_.Length, wholeLength - _.Start))).ToList(), column);
        }

        var from = 0;
        if (matches.Count > 0)
        {
            from = Math.Max(0, matches[0].Start - beforeMatch);
        }

        var to = Math.Min(whole.Length, from + longestLine);
        var prefix = "";
        if (from > 0)
        {
            prefix = "…";
        }

        var suffix = "";
        if (to < whole.Length)
        {
            suffix = "…";
        }

        var kept = new List<(int Start, int Length)>();
        foreach (var (start, length) in matches)
        {
            var clippedStart = Math.Max(start, from);
            var clippedEnd = Math.Min(start + length, to);
            if (clippedEnd > clippedStart ||
                (length == 0 && start >= from && start <= to))
            {
                kept.Add((clippedStart - from + prefix.Length, Math.Max(0, clippedEnd - clippedStart)));
            }
        }

        return new(number, $"{prefix}{whole[from..to]}{suffix}", kept, column);
    }
}
