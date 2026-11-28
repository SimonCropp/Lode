/// <summary>
/// What Replace would make of a file found, shown from Find Results in Lode's diff view before
/// anything is replaced: the file as it is against the file as Replace would leave it, with what
/// the Replace box says, the lines taken out of the results left as they are. The diff view shows
/// the patches git writes, and has no diff of two texts of its own, so the preview writes one:
/// Replace knows where each replacement is, so the lines each changes are the lines it is on, and
/// no search for what the two texts share is needed. Written as git writes a patch, with three
/// lines either side of each change, the diff view reads it as it reads git's, so it lays out,
/// marks what changed within each line, and shows more of the file, as for any other diff.
/// </summary>
sealed record ReplacePreview(FileResult File, Matcher Matcher, string Replacement, bool PreserveCase)
{
    // How many lines are shown either side of a change, as git shows them.
    const int context = 3;

    /// <summary>
    /// What the diff view is to show: the file, by its name in its folder, as one modified.
    /// </summary>
    public DiffSource ToSource() =>
        new(Path.GetDirectoryName(File.Path) ?? File.Path, new(Path.GetFileName(File.Path), null, GitChange.Modified))
        {
            Preview = this
        };

    /// <summary>
    /// The diff, read from the file as it is now, and the lines of the file as Replace would
    /// leave it, for showing more of it. Too big to show straight away unless
    /// <paramref name="evenIfLarge"/>. A file Replace would leave alone says why, as a
    /// <see cref="FailedDiff"/>.
    /// </summary>
    public (FileDiff Diff, IReadOnlyList<string>? Lines) Read(bool evenIfLarge)
    {
        ReplaceInFiles.Planned plan;
        try
        {
            plan = ReplaceInFiles.Plan(File, Matcher, Replacement, PreserveCase);
        }
        catch (Exception exception)
            when (ReplaceInFiles.WhyLeft(exception) is { } why)
        {
            return (new FailedDiff(why), null);
        }

        var name = Path.GetFileName(File.Path);
        var patch = Patch(name, plan.Before, plan.After, plan.Replaced, plan.Ranges);
        return (DiffParser.Parse(patch, evenIfLarge), DiffExpansion.LinesOf(plan.After));
    }

    /// <summary>
    /// The patch git would write between <paramref name="before"/> and <paramref name="after"/>,
    /// given where each replacement was in the one, <paramref name="from"/>, and is in the other,
    /// <paramref name="to"/>, in the same order.
    /// </summary>
    internal static string Patch(string name, string before, string after, IReadOnlyList<(int Start, int Length)> from, IReadOnlyList<(int Start, int Length)> to)
    {
        var oldLines = new Lines(before);
        var newLines = new Lines(after);
        var changes = Changes(oldLines, newLines, from, to);
        var patch = new StringBuilder();
        patch.Append($"--- a/{name}\n+++ b/{name}\n");
        var first = 0;
        while (first < changes.Count)
        {
            // The changes close enough together for the lines between them to be shown whole
            // are one hunk, as git makes them.
            var last = first;
            while (last + 1 < changes.Count &&
                   changes[last + 1].OldFirst - changes[last].OldEnd <= context * 2)
            {
                last++;
            }

            WriteHunk(patch, oldLines, newLines, changes, first, last);
            first = last + 1;
        }

        return patch.ToString();
    }

    // Lines OldFirst up to OldEnd of the old text, replaced by lines NewFirst up to NewEnd of
    // the new, counted from 0, either run empty.
    readonly record struct Change(int OldFirst, int OldEnd, int NewFirst, int NewEnd);

    // The lines each replacement changes, those of replacements sharing a line together, less
    // any each side still has the same at the start or end, a line a replacement leaves as it
    // was, or a line ending it takes away and puts back.
    static List<Change> Changes(Lines oldLines, Lines newLines, IReadOnlyList<(int Start, int Length)> from, IReadOnlyList<(int Start, int Length)> to)
    {
        var changes = new List<Change>();
        Change? open = null;
        for (var index = 0; index < from.Count; index++)
        {
            // From the line the replacement starts on to the line what follows it starts on,
            // which it joins: the line after the last, beyond the file, once at its end.
            var (oldStart, oldLength) = from[index];
            var (newStart, newLength) = to[index];
            var change = new Change(
                oldLines.LineAt(oldStart),
                oldLines.LineAt(oldStart + oldLength) + 1,
                newLines.LineAt(newStart),
                newLines.LineAt(newStart + newLength) + 1);
            if (open is { } shared &&
                change.OldFirst < shared.OldEnd)
            {
                open = shared with
                {
                    OldEnd = change.OldEnd,
                    NewEnd = change.NewEnd
                };
                continue;
            }

            Add(changes, open, oldLines, newLines);
            open = change;
        }

        Add(changes, open, oldLines, newLines);
        return changes;
    }

    static void Add(List<Change> changes, Change? open, Lines oldLines, Lines newLines)
    {
        if (open is not { } change)
        {
            return;
        }

        var (oldFirst, oldEnd, newFirst, newEnd) = change;
        oldEnd = Math.Min(oldEnd, oldLines.Count);
        newEnd = Math.Min(newEnd, newLines.Count);
        while (oldFirst < oldEnd &&
               newFirst < newEnd &&
               oldLines.Whole(oldFirst).SequenceEqual(newLines.Whole(newFirst)))
        {
            oldFirst++;
            newFirst++;
        }

        while (oldFirst < oldEnd &&
               newFirst < newEnd &&
               oldLines.Whole(oldEnd - 1).SequenceEqual(newLines.Whole(newEnd - 1)))
        {
            oldEnd--;
            newEnd--;
        }

        if (oldFirst == oldEnd &&
            newFirst == newEnd)
        {
            return;
        }

        // Right after the one before, with no line between them, the two are one run, its
        // lines deleted before those added, as git writes them.
        if (changes.Count > 0 &&
            changes[^1].OldEnd == oldFirst)
        {
            changes[^1] = changes[^1] with
            {
                OldEnd = oldEnd,
                NewEnd = newEnd
            };
            return;
        }

        changes.Add(new(oldFirst, oldEnd, newFirst, newEnd));
    }

    // The hunk of changes first to last: the lines before the first, each change's old lines
    // deleted and its new lines added, the lines between, and the lines after the last. The
    // lines between changes are the same on either side.
    static void WriteHunk(StringBuilder patch, Lines oldLines, Lines newLines, List<Change> changes, int first, int last)
    {
        var start = changes[first];
        var end = changes[last];
        var oldFrom = Math.Max(0, start.OldFirst - context);
        var newFrom = start.NewFirst - (start.OldFirst - oldFrom);
        var oldTo = Math.Min(oldLines.Count, end.OldEnd + context);
        var newTo = end.NewEnd + (oldTo - end.OldEnd);
        patch.Append($"@@ -{Range(oldFrom, oldTo)} +{Range(newFrom, newTo)} @@\n");
        var at = oldFrom;
        for (var index = first; index <= last; index++)
        {
            var change = changes[index];
            for (; at < change.OldFirst; at++)
            {
                WriteLine(patch, ' ', oldLines, at);
            }

            for (var line = change.OldFirst; line < change.OldEnd; line++)
            {
                WriteLine(patch, '-', oldLines, line);
            }

            for (var line = change.NewFirst; line < change.NewEnd; line++)
            {
                WriteLine(patch, '+', newLines, line);
            }

            at = change.OldEnd;
        }

        for (; at < oldTo; at++)
        {
            WriteLine(patch, ' ', oldLines, at);
        }
    }

    // Where a run of lines starts, from 1, and how many: git has a run of none start at the line
    // before it.
    static string Range(int from, int to)
    {
        var count = to - from;
        if (count == 0)
        {
            return $"{from},0";
        }

        return $"{from + 1},{count}";
    }

    // A line as git writes it: its +, - or space, the line without its line feed, a carriage
    // return before that kept, as git keeps it, and, for the last line of a file that doesn't
    // end with a line ending, git's note that it doesn't.
    static void WriteLine(StringBuilder patch, char kind, Lines lines, int line)
    {
        var whole = lines.Whole(line);
        var ended = whole.EndsWith('\n');
        if (ended)
        {
            whole = whole[..^1];
        }

        patch.Append(kind).Append(whole).Append('\n');
        if (!ended)
        {
            patch.Append("\\ No newline at end of file\n");
        }
    }

    // A text's lines, each ending with its line feed, but the last where the text doesn't end
    // with one.
    sealed class Lines
    {
        string text;
        List<int> starts = [0];

        public Lines(string text)
        {
            this.text = text;
            for (var index = text.IndexOf('\n'); index >= 0 && index + 1 < text.Length; index = text.IndexOf('\n', index + 1))
            {
                starts.Add(index + 1);
            }

            Count = text.Length == 0 ? 0 : starts.Count;
        }

        public int Count { get; }

        /// <summary>
        /// The line a position is on, from 0: after a line feed, the next line, which is
        /// <see cref="Count"/>, beyond the last, at the end of a text ending with one.
        /// </summary>
        public int LineAt(int position)
        {
            if (Count == 0)
            {
                return 0;
            }

            var index = starts.BinarySearch(position);
            if (index < 0)
            {
                index = ~index - 1;
            }

            if (position == text.Length &&
                text[^1] == '\n')
            {
                return Count;
            }

            return index;
        }

        public CharSpan Whole(int line)
        {
            var end = text.Length;
            if (line + 1 < starts.Count)
            {
                end = starts[line + 1];
            }

            return text.AsSpan(starts[line], end - starts[line]);
        }
    }
}
