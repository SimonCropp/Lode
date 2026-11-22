/// <summary>
/// What a Find in Files search has found, as rows, laid out as Sublime lays out its Find Results:
/// a heading saying what was looked for, each file's full path, then its lines, each numbered,
/// a colon after the number of one that matches, ".." where lines are left out between them,
/// and at the end how many matches across how many files. Added to as the search goes, and
/// never otherwise changed but for a file taken out, so the list showing it only ever has rows
/// added at the end. Used on the UI thread.
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

    public void Add(FileResult result)
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

        starts.Add(headingRows + fileRows);
        files.Add(new(result, [.. rows]));

        // Its own row, its lines and gaps, and a blank after.
        fileRows += rows.Count + 2;
    }

    /// <summary>
    /// Takes a file out, so it isn't shown, nor replaced in.
    /// </summary>
    public void Remove(int file)
    {
        files.RemoveAt(file);
        starts.Clear();
        fileRows = 0;
        foreach (var found in files)
        {
            starts.Add(headingRows + fileRows);
            fileRows += found.Rows.Length + 2;
        }
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

        for (var index = from; index >= 0 && index < RowCount; index += step)
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

        var matches = files.Sum(_ => _.Result.MatchCount);
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
        for (var index = 0; index < RowCount; index++)
        {
            builder.Append(TextOf(RowAt(index)).TrimEnd()).Append('\n');
        }

        return builder.ToString();
    }
}
