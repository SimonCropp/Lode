/// <summary>
/// What a Find in Files search has found, as rows, laid out as Sublime lays out its Find Results:
/// a heading saying what was looked for, each file's full path, then its lines, each numbered,
/// a colon after the number of one that matches, ".." where lines are left out between them,
/// and at the end how many matches across how many files. Added to as the search goes, and
/// never otherwise changed but for a file or a line taken out, so the list showing it only ever
/// has rows added at the end while it searches. Used on the UI thread.
/// </summary>
sealed class FindResults(FindQuery query)
{
    public enum RowKind
    {
        Heading,
        Blank,
        File,
        Line,
        Gap,
        Summary
    }

    /// <param name="File">Which file the row is of, for a file, line or gap row; otherwise -1.</param>
    /// <param name="Line">Which of the file's lines, for a line row; otherwise -1.</param>
    /// <param name="Summary">Which line of the summary, for a summary row.</param>
    public record struct Row(RowKind Kind, int File = -1, int Line = -1, int Summary = -1);

    // A file found, and its rows after its own: for each, the line it shows, or -1 for a gap.
    sealed record Found(FileResult Result, int[] Rows);

    List<Found> files = [];

    // The row each file's own row is, in order, so finding which file a row is of is a binary
    // search rather than counting.
    List<int> starts = [];
    int fileRows;

    public FindQuery Query { get; } = query;

    public IEnumerable<FileResult> Files => files.Select(_ => _.Result);

    public int FileCount => files.Count;

    public FileResult FileAt(int index) => files[index].Result;

    /// <summary>
    /// How far the search has got, and, once it is done, what it left out.
    /// </summary>
    public ContentSearch.Counts Counts { get; set; } = new();

    public SearchWalk.Report Report { get; set; } = new();

    /// <summary>
    /// Whether the search has finished: searched everything, been stopped, or failed.
    /// </summary>
    public bool Done { get; set; }

    /// <summary>
    /// The search was stopped before it had searched everything.
    /// </summary>
    public bool Stopped { get; set; }

    /// <summary>
    /// Why there are no results: nothing to find, a pattern that can't be read, a folder that
    /// isn't there.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// What Replace did, once it has: how many it replaced, in how many files, and with what. The
    /// lines shown are then the lines as they are now, the replacements marked.
    /// </summary>
    public (string With, int Replaced, int Files, int Failed)? Replaced { get; set; }

    // The heading and the blank under it.
    const int headingRows = 2;

    public int RowCount => headingRows + fileRows + SummaryLines().Count;

    // How many matches the files hold between them, counted as they come and go: the summary
    // says it, and is asked for with every row drawn.
    int matches;

    public void Add(FileResult result)
    {
        var found = Rows(result);
        starts.Add(headingRows + fileRows);
        files.Add(found);
        matches += result.MatchCount;
        fileRows += RowsOf(found);
    }

    static Found Rows(FileResult result)
    {
        var rows = new List<int>(result.Lines.Count + 2);
        for (var index = 0; index < result.Lines.Count; index++)
        {
            if (index > 0 &&
                result.Lines[index].Number > result.Lines[index - 1].Number + 1)
            {
                rows.Add(-1);
            }

            rows.Add(index);
        }

        return new(result, [.. rows]);
    }

    // Its own row, its lines and gaps, and a blank after.
    static int RowsOf(Found found) =>
        found.Rows.Length + 2;

    /// <summary>
    /// Takes a file out, so it isn't shown, nor replaced in.
    /// </summary>
    public void Remove(int file)
    {
        matches -= files[file].Result.MatchCount;
        files.RemoveAt(file);
        Restart();
    }

    // Where each file's rows start, worked out again once one has fewer.
    void Restart()
    {
        starts.Clear();
        fileRows = 0;
        foreach (var found in files)
        {
            starts.Add(headingRows + fileRows);
            fileRows += RowsOf(found);
        }
    }

    /// <summary>
    /// Takes a line that matches out of a file's lines, so it isn't shown, and Replace leaves
    /// what is on it as it is, the rest of the file replaced. A match over several lines is on
    /// each, so is left whole, and the others it is on go too, where nothing else on them is
    /// replaced; as do the lines of context shown for them alone. The file goes once none of
    /// its lines that match are left. False for a line of context, which has nothing to leave.
    /// </summary>
    public bool RemoveLine(int file, int line)
    {
        var result = files[file].Result;
        var taken = result.Lines[line];
        if (taken.IsContext)
        {
            return false;
        }

        IReadOnlyList<ResultLine> skipped = [.. result.Skipped, taken];

        bool IsLeft(int match, IReadOnlyList<ResultLine> left) =>
            left.Any(_ => match >= _.FirstMatch && match <= _.LastMatch);

        bool IsAllLeft(ResultLine shown)
        {
            for (var match = shown.FirstMatch; match <= shown.LastMatch; match++)
            {
                if (!IsLeft(match, skipped))
                {
                    return false;
                }
            }

            return true;
        }

        var matching = result.Lines
            .Where(_ => !_.IsContext && !IsAllLeft(_))
            .Select(_ => _.Number)
            .ToList();
        if (matching.Count == 0)
        {
            Remove(file);
            return true;
        }

        var lines = new List<ResultLine>(result.Lines.Count);
        foreach (var shown in result.Lines)
        {
            if (shown.IsContext)
            {
                if (matching.Any(_ => Math.Abs(_ - shown.Number) <= Query.ContextLines))
                {
                    lines.Add(shown);
                }

                continue;
            }

            if (matching.Contains(shown.Number))
            {
                lines.Add(shown);
            }
        }

        // Counted once, whichever line a match is on is taken out first.
        var leaving = 0;
        for (var match = taken.FirstMatch; match <= taken.LastMatch; match++)
        {
            if (!IsLeft(match, result.Skipped))
            {
                leaving++;
            }
        }

        var kept = result with
        {
            Lines = lines,
            MatchCount = result.MatchCount - leaving,
            Skipped = skipped
        };
        matches -= leaving;
        files[file] = Rows(kept);
        Restart();
        return true;
    }

    public Row RowAt(int index)
    {
        if (index == 0)
        {
            return new(RowKind.Heading);
        }

        if (index < headingRows)
        {
            return new(RowKind.Blank);
        }

        if (index >= headingRows + fileRows)
        {
            return new(RowKind.Summary, Summary: index - headingRows - fileRows);
        }

        var file = starts.BinarySearch(index);
        if (file >= 0)
        {
            return new(RowKind.File, file);
        }

        file = ~file - 1;
        var offset = index - starts[file] - 1;
        var rows = files[file].Rows;
        if (offset >= rows.Length)
        {
            return new(RowKind.Blank, file);
        }

        if (rows[offset] < 0)
        {
            return new(RowKind.Gap, file);
        }

        return new(RowKind.Line, file, rows[offset]);
    }

    /// <summary>
    /// The row a file's own row is.
    /// </summary>
    public int RowOfFile(int file) => starts[file];

    /// <summary>
    /// The first line with a match in it at or after <paramref name="from"/>, or before it going
    /// back, or -1 when there is none that way.
    /// </summary>
    public int NextMatchRow(int from, bool forward)
    {
        var step = 1;
        if (!forward)
        {
            step = -1;
        }

        // Counted the once: the count makes the summary, which is not for every row passed.
        var count = RowCount;
        for (var index = from; index >= 0 && index < count; index += step)
        {
            var row = RowAt(index);
            if (row.Kind == RowKind.Line &&
                !FileAt(row.File).Lines[row.Line].IsContext)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// What the heading row says.
    /// </summary>
    public string Heading
    {
        get
        {
            if (Replaced is { } replaced)
            {
                return $"Replaced \"{Query.Find}\" with \"{replaced.With}\"{Query.Describe()}";
            }

            if (Done)
            {
                return $"Searched {Plural(Counts.Searched, "file")} for \"{Query.Find}\"{Query.Describe()}";
            }

            return $"Searching for \"{Query.Find}\"{Query.Describe()}";
        }
    }

    /// <summary>
    /// What the rows at the end say: how many matches across how many files, or how far the
    /// search has got, and what it left out.
    /// </summary>
    public IReadOnlyList<string> SummaryLines()
    {
        if (Error != null)
        {
            return [Error];
        }

        var lines = new List<string>();
        if (Replaced is { } replaced)
        {
            lines.Add($"{Plural(replaced.Replaced, "occurrence")} replaced across {Plural(replaced.Files, "file")}");
            if (replaced.Failed > 0)
            {
                lines.Add($"{Plural(replaced.Failed, "file")} couldn't be written: see the Actions panel");
            }

            return lines;
        }

        if (Done)
        {
            lines.Add($"{Plural(matches, "match", "matches")} across {Plural(files.Count, "file")}");
        }
        else
        {
            lines.Add($"Searching… {Counts.Searched:N0} files so far, {Plural(matches, "match", "matches")} in {Plural(files.Count, "file")}");
        }

        if (Stopped)
        {
            lines.Add("Stopped before searching everything");
        }

        var leftOut = new List<string>();
        if (Counts.Binary > 0)
        {
            leftOut.Add($"{Plural(Counts.Binary, "binary file")}");
        }

        if (Counts.TooLarge > 0)
        {
            leftOut.Add($"{Plural(Counts.TooLarge, "file")} over {TextFile.MostBytes / 1024 / 1024} MB");
        }

        if (Counts.Unreadable > 0)
        {
            leftOut.Add($"{Plural(Counts.Unreadable, "file")} that couldn't be read");
        }

        if (Counts.TimedOut > 0)
        {
            leftOut.Add($"{Plural(Counts.TimedOut, "file")} the pattern took too long over");
        }

        if (leftOut.Count > 0)
        {
            lines.Add($"Left out: {string.Join(", ", leftOut)}");
        }

        foreach (var repo in Report.Unlisted)
        {
            lines.Add($"Git couldn't list what it ignores in {repo}, so all of it was searched");
        }

        if (Counts.LinesLeftOut)
        {
            lines.Add($"Only the first {ContentSearch.MostLines:N0} lines are shown");
        }

        return lines;
    }

    static string Plural(int count, string one, string? many = null)
    {
        if (count == 1)
        {
            return $"1 {one}";
        }

        return $"{count:N0} {many ?? one + "s"}";
    }

    /// <summary>
    /// What a row says, as text: what Copy gives, and the list draws.
    /// </summary>
    public string TextOf(Row row)
    {
        switch (row.Kind)
        {
            case RowKind.Heading:
                return Heading;
            case RowKind.File:
            {
                var result = FileAt(row.File);
                if (result.LinesLeftOut)
                {
                    return $"{result.Path}: {Plural(result.MatchCount, "match", "matches")}, not shown";
                }

                return $"{result.Path}:";
            }
            case RowKind.Line:
            {
                var line = FileAt(row.File).Lines[row.Line];
                return $"{LineNumber(line)} {line.Text}";
            }
            case RowKind.Gap:
                return "   ..";
            case RowKind.Summary:
                return SummaryLines()[row.Summary];
            default:
                return "";
        }
    }

    /// <summary>
    /// A line's number as the rows show it: right aligned, with a colon after it for a line that
    /// matches, a space for one that is context.
    /// </summary>
    public static string LineNumber(ResultLine line)
    {
        var mark = ':';
        if (line.IsContext)
        {
            mark = ' ';
        }

        return $"{line.Number,5}{mark}";
    }

    /// <summary>
    /// Every row, one a line: Copy all results.
    /// </summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        var count = RowCount;
        for (var index = 0; index < count; index++)
        {
            builder.Append(TextOf(RowAt(index)).TrimEnd()).Append('\n');
        }

        return builder.ToString();
    }
}
