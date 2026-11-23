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
                var before = deleted[index].Content;
                var after = added[index].Content;
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
    /// changed-range.ts. Lines that are the same have nothing changed.
    /// </summary>
    public static (ChangedRange? Before, ChangedRange? After) Changed(string before, string after)
    {
        var max = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < max &&
               before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max - prefix &&
               before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
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
}
