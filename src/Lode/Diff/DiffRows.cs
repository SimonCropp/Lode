/// <summary>
/// What a row of a drawn diff is: a hunk's heading, a line both sides have, one added, one
/// deleted, or, side by side, one deleted beside the one that took its place.
/// </summary>
enum DiffRowKind
{
    Hunk,
    Context,
    Added,
    Deleted,
    Modified
}

/// <summary>
/// The part of a line that changed, from its start, in characters of its content.
/// </summary>
readonly record struct ChangedRange(int Start, int Length);

/// <summary>
/// A row of a drawn diff: the hunk it is in, the line on the old side and the line on the new,
/// either missing for an added or deleted line, and on each the part that changed, where it is
/// shown.
/// </summary>
sealed record DiffRow(
    DiffRowKind Kind,
    int HunkIndex,
    DiffLine? Before,
    DiffLine? After,
    ChangedRange? BeforeChanged = null,
    ChangedRange? AfterChanged = null);

/// <summary>
/// Lays a diff's hunks out as rows, as GitHub Desktop's side-by-side-diff.tsx does: each hunk's
/// heading, then its lines, each run of added and deleted lines split into the deleted and the
/// added. One above the other, every deleted line of a run comes before every added one. Side by
/// side, each deleted line is beside the added line in the same place in the run, and what is left
/// over of either is on its own.
/// </summary>
static class DiffRows
{
    /// <summary>
    /// Lines longer than this have no part shown changed, as GitHub Desktop's
    /// MaxIntraLineDiffStringLength.
    /// </summary>
    public const int MaxChangedLineLength = 1024;

    public static List<DiffRow> Build(IReadOnlyList<DiffHunk> hunks, bool split)
    {
        var rows = new List<DiffRow>();
        for (var hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
        {
            var hunk = hunks[hunkIndex];
            rows.Add(new(DiffRowKind.Hunk, hunkIndex, null, null));
            var deleted = new List<DiffLine>();
            var added = new List<DiffLine>();
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Delete:
                        deleted.Add(line);
                        break;
                    case DiffLineKind.Add:
                        added.Add(line);
                        break;
                    default:
                        AddRun(rows, hunkIndex, deleted, added, split);
                        rows.Add(new(DiffRowKind.Context, hunkIndex, line, line));
                        break;
                }
            }

            AddRun(rows, hunkIndex, deleted, added, split);
        }

        return rows;
    }

    // A run of deleted and added lines, its lines shown changed only where there are as many of
    // each, to match github.com, as GitHub Desktop says.
    static void AddRun(List<DiffRow> rows, int hunkIndex, List<DiffLine> deleted, List<DiffLine> added, bool split)
    {
        if (deleted.Count == 0 &&
            added.Count == 0)
        {
            return;
        }

        var showChanged = deleted.Count == added.Count;
        var beforeRanges = new ChangedRange?[deleted.Count];
        var afterRanges = new ChangedRange?[added.Count];
        if (showChanged)
        {
            for (var index = 0; index < deleted.Count; index++)
            {
                var before = deleted[index].ContentSpan;
                var after = added[index].ContentSpan;
                if (before.Length >= MaxChangedLineLength ||
                    after.Length >= MaxChangedLineLength)
                {
                    continue;
                }

                var (beforeRange, afterRange) = Changed(before, after);
                beforeRanges[index] = beforeRange;
                afterRanges[index] = afterRange;
            }
        }

        if (split)
        {
            var paired = Math.Min(deleted.Count, added.Count);
            for (var index = 0; index < paired; index++)
            {
                rows.Add(new(DiffRowKind.Modified, hunkIndex, deleted[index], added[index], beforeRanges[index], afterRanges[index]));
            }

            for (var index = paired; index < deleted.Count; index++)
            {
                rows.Add(new(DiffRowKind.Deleted, hunkIndex, deleted[index], null, beforeRanges[index]));
            }

            for (var index = paired; index < added.Count; index++)
            {
                rows.Add(new(DiffRowKind.Added, hunkIndex, null, added[index], null, afterRanges[index]));
            }
        }
        else
        {
            for (var index = 0; index < deleted.Count; index++)
            {
                rows.Add(new(DiffRowKind.Deleted, hunkIndex, deleted[index], null, beforeRanges[index]));
            }

            for (var index = 0; index < added.Count; index++)
            {
                rows.Add(new(DiffRowKind.Added, hunkIndex, null, added[index], null, afterRanges[index]));
            }
        }

        deleted.Clear();
        added.Clear();
    }

    /// <summary>
    /// The part of each line that differs from the other: what is left once the start they share
    /// and the end they share are taken off, as GitHub Desktop's relativeChanges in
    /// changed-range.ts, each changed whole what draws as one character. Lines that are the same
    /// have nothing changed.
    /// </summary>
    public static (ChangedRange? Before, ChangedRange? After) Changed(CharSpan before, CharSpan after)
    {
        var max = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < max &&
               before[prefix] == after[prefix])
        {
            prefix++;
        }

        // Compared a character at a time, the start shared can end part way through what draws
        // as one: in the middle of an emoji, two characters, when one changed to another sharing
        // the first of them, or between a letter and the accent on it, each half then drawn on
        // its own, as a box. Taken back to before it. What comes before is the same in both, so
        // where it can be split is too, but for where they differ.
        prefix = Math.Min(SplitAtOrBefore(before, prefix), SplitAtOrBefore(after, prefix));

        var suffix = 0;
        while (suffix < max - prefix &&
               before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        // And the end shared taken on to after it. Whether the end shared can be split from what
        // is before it can hang on what is before that, as in a run of flags, so the shorter is
        // tried again in the other until both can be.
        while (suffix > 0)
        {
            var shared = Math.Min(
                before.Length - SplitAtOrAfter(before, before.Length - suffix),
                after.Length - SplitAtOrAfter(after, after.Length - suffix));
            if (shared == suffix)
            {
                break;
            }

            suffix = shared;
        }

        var beforeLength = before.Length - prefix - suffix;
        var afterLength = after.Length - prefix - suffix;
        ChangedRange? beforeRange = null;
        if (beforeLength > 0)
        {
            beforeRange = new(prefix, beforeLength);
        }

        ChangedRange? afterRange = null;
        if (afterLength > 0)
        {
            afterRange = new(prefix, afterLength);
        }

        return (beforeRange, afterRange);
    }

    // The last place at or before index a line can be split without splitting what draws as one
    // character: a surrogate pair, a letter and the marks on it, an emoji of several joined.
    static int SplitAtOrBefore(CharSpan line, int index)
    {
        if (SplitsPlainly(line, index))
        {
            return index;
        }

        var at = 0;
        while (true)
        {
            var next = at + StringInfo.GetNextTextElementLength(line[at..]);
            if (next > index)
            {
                return at;
            }

            at = next;
        }
    }

    // The first place at or after index a line can be split, likewise.
    static int SplitAtOrAfter(CharSpan line, int index)
    {
        if (SplitsPlainly(line, index))
        {
            return index;
        }

        var at = 0;
        while (at < index)
        {
            at += StringInfo.GetNextTextElementLength(line[at..]);
        }

        return at;
    }

    // Whether a line can be split at index without going through it from its start, as where
    // one character draws starts and ends can hang on those before it: at either end, and
    // between two characters of ASCII, as most lines are, but a carriage return and the line
    // feed after it.
    static bool SplitsPlainly(CharSpan line, int index) =>
        index <= 0 ||
        index >= line.Length ||
        (char.IsAscii(line[index - 1]) &&
         char.IsAscii(line[index]) &&
         !(line[index - 1] == '\r' && line[index] == '\n'));

    /// <summary>
    /// How many columns the widest line of <paramref name="hunks"/> takes, for how far the diff
    /// scrolls sideways.
    /// </summary>
    public static int Widest(IReadOnlyList<DiffHunk> hunks)
    {
        var widest = 0;
        foreach (var hunk in hunks)
        {
            foreach (var line in hunk.Lines)
            {
                widest = Math.Max(widest, Columns(line.ContentSpan));
            }
        }

        return widest;
    }

    /// <summary>
    /// The column <paramref name="text"/> ends in, starting in <paramref name="column"/>: a
    /// column a character, a tab to the next multiple of four.
    /// </summary>
    public static int Columns(CharSpan text, int column = 0)
    {
        // Without tabs, as most lines are, a column a character.
        if (!text.Contains('\t'))
        {
            return column + text.Length;
        }

        foreach (var character in text)
        {
            if (character == '\t')
            {
                column += 4 - column % 4;
                continue;
            }

            column++;
        }

        return column;
    }
}

/// <summary>
/// A text diff laid out to draw, which takes as long as the diff is big, so is done as it is
/// read, off the UI thread: its hunks, with the one below the last for the rest of the file
/// given the file's <paramref name="Lines"/>, the rows they make, one above the other or side
/// by side, and how many columns the widest line takes.
/// </summary>
sealed record DiffLayout(TextDiff Diff, IReadOnlyList<string>? Lines, bool Split, IReadOnlyList<DiffHunk> Hunks, List<DiffRow> Rows, int Widest)
{
    public static DiffLayout Of(TextDiff diff, IReadOnlyList<string>? lines, bool split)
    {
        var hunks = diff.Hunks;
        if (lines != null)
        {
            var oldCount = lines.Count - diff.Added + diff.Deleted;
            hunks = DiffExpansion.WithBottom(hunks, oldCount, lines.Count);
        }

        return new(diff, lines, split, hunks, DiffRows.Build(hunks, split), DiffRows.Widest(hunks));
    }
}
