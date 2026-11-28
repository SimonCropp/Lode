/// <summary>
/// Shows more of the file around a diff's hunks, as GitHub Desktop's text-diff-expansion.ts does:
/// 20 lines at a time above or below a hunk, from the file's lines, a hunk merged into the one
/// beside it once nothing is left between them, and a hunk of no lines below the last for the
/// rest of the file. What an expansion brings in is context, never picked to commit.
/// </summary>
static class DiffExpansion
{
    /// <summary>
    /// How many lines an arrow shows, and the most a gap between hunks can be to show all of it
    /// with one.
    /// </summary>
    const int defaultStep = 20;

    /// <summary>
    /// The most a file can be, in bytes, for its diff to be expanded, as GitHub Desktop has it:
    /// beyond, its lines aren't read.
    /// </summary>
    public const int MaxFileLength = 1024 * 1024;

    /// <summary>
    /// What can be expanded above the hunk at <paramref name="index"/>: above the first unless it
    /// starts the file; the whole gap at once when it is short; otherwise up or down.
    /// </summary>
    public static HunkExpansion ExpansionOf(int index, HunkHeader header, DiffHunk? previous)
    {
        if (index == 0 ||
            previous == null)
        {
            if (header is { OldStart: > 1, NewStart: > 1 })
            {
                return HunkExpansion.Up;
            }

            return HunkExpansion.None;
        }

        var gap = header.OldStart - previous.Header.OldStart - previous.Header.OldCount;
        if (gap <= defaultStep)
        {
            return HunkExpansion.Short;
        }

        return HunkExpansion.Both;
    }

    /// <summary>
    /// <paramref name="hunks"/> with a hunk of no lines after the last, for the arrow that shows
    /// the rest of the file, when the last ends before the file does.
    /// </summary>
    public static IReadOnlyList<DiffHunk> WithBottom(IReadOnlyList<DiffHunk> hunks, int oldLineCount, int newLineCount)
    {
        if (hunks.Count == 0 ||
            hunks[^1].IsBottom)
        {
            return hunks;
        }

        var last = hunks[^1].Header;
        var newEnd = last.NewStart + last.NewCount;
        if (newEnd > newLineCount)
        {
            return hunks;
        }

        var oldStart = last.OldStart + last.OldCount;
        var header = new HunkHeader(oldStart, oldLineCount - oldStart + 1, newEnd, newLineCount - newEnd + 1);
        return [.. hunks, new(header, "", [], HunkExpansion.Down)];
    }

    /// <summary>
    /// <paramref name="hunks"/> with up to <paramref name="step"/> more lines of
    /// <paramref name="newLines"/> above or below the hunk at <paramref name="index"/>, merged
    /// with the hunk beside it once they meet. Null when there is nothing more to show.
    /// </summary>
    public static IReadOnlyList<DiffHunk>? Expand(IReadOnlyList<DiffHunk> hunks, int index, bool up, IReadOnlyList<string> newLines, int step = defaultStep)
    {
        if (index < 0 ||
            index >= hunks.Count)
        {
            return null;
        }

        var (header, _, hunkLines, _) = hunks[index];
        DiffHunk? adjacent = null;
        var adjacentIndex = -1;
        if (up &&
            index > 0)
        {
            adjacentIndex = index - 1;
        }
        else if (!up &&
                 index < hunks.Count - 1)
        {
            adjacentIndex = index + 1;
        }

        if (adjacentIndex >= 0)
        {
            adjacent = hunks[adjacentIndex];
        }

        var adjacentIsBottom = adjacent != null &&
                               !up &&
                               adjacent.IsBottom &&
                               adjacentIndex == hunks.Count - 1;

        int from;
        int to;
        if (up)
        {
            from = header.NewStart - step;
            to = header.NewStart;
        }
        else
        {
            from = header.NewStart + header.NewCount;
            to = from + step;
        }

        var merge = false;
        if (adjacent != null)
        {
            if (up)
            {
                var limit = adjacent.Header.NewStart + adjacent.Header.NewCount;
                from = Math.Max(from, limit);
                merge = from == limit;
            }
            else if (!adjacentIsBottom)
            {
                var limit = adjacent.Header.NewStart;
                to = Math.Min(to, limit);
                merge = to == limit;
            }
        }

        var start = Math.Max(from - 1, 0);
        var end = Math.Min(to - 1, newLines.Count);
        var count = end - start;
        if (count <= 0)
        {
            return null;
        }

        var added = new List<DiffLine>(count);
        for (var offset = 0; offset < count; offset++)
        {
            int newNumber;
            int oldNumber;
            if (up)
            {
                newNumber = header.NewStart - (count - offset);
                oldNumber = header.OldStart - (count - offset);
            }
            else
            {
                newNumber = header.NewStart + header.NewCount + offset;
                oldNumber = header.OldStart + header.OldCount + offset;
            }

            added.Add(new(DiffLineKind.Context, " " + newLines[start + offset], null, oldNumber, newNumber));
        }

        var newHeader = header with
        {
            OldCount = header.OldCount + count,
            NewCount = header.NewCount + count
        };
        if (up)
        {
            newHeader = newHeader with
            {
                OldStart = header.OldStart - count,
                NewStart = header.NewStart - count
            };
        }

        IReadOnlyList<DiffLine> lines;
        if (up)
        {
            lines = [.. added, .. hunkLines];
        }
        else
        {
            lines = [.. hunkLines, .. added];
        }

        DiffHunk? previous = null;
        if (index > 0)
        {
            previous = hunks[index - 1];
        }

        var updated = new DiffHunk(newHeader, newHeader.ToString(), lines, ExpansionOf(index, newHeader, previous));
        int before;
        int after;
        if (merge &&
            adjacent != null)
        {
            if (up)
            {
                updated = Merge(adjacent, updated);
                before = index - 1;
                after = index + 1;
            }
            else
            {
                updated = Merge(updated, adjacent);
                before = index;
                after = index + 2;
            }
        }
        else
        {
            before = index;
            after = index + 1;
        }

        var result = new List<DiffHunk>(hunks.Count);
        for (var i = 0; i < before; i++)
        {
            result.Add(hunks[i]);
        }

        result.Add(updated);

        // Once the hunk reaches the end of the file, the hunk below it for the rest of the file
        // has nothing left to show.
        var lastLine = newHeader.NewStart + newHeader.NewCount - 1;
        if (lastLine >= newLines.Count)
        {
            return result;
        }

        for (var i = after; i < hunks.Count; i++)
        {
            var following = hunks[i];
            if (i == after &&
                !following.IsBottom)
            {
                following = following with
                {
                    Expansion = ExpansionOf(result.Count, following.Header, updated)
                };
            }

            result.Add(following);
        }

        return result;
    }

    /// <summary>
    /// <paramref name="hunks"/> expanded until they are one hunk of the whole file. Null when
    /// they can't be.
    /// </summary>
    public static IReadOnlyList<DiffHunk>? ExpandWhole(IReadOnlyList<DiffHunk> hunks, IReadOnlyList<string> newLines)
    {
        var result = hunks;
        while (result.Count > 1 ||
               result is [{ Expansion: HunkExpansion.Up }])
        {
            var first = result[0];
            var expanded = Expand(result, 0, first.Expansion == HunkExpansion.Up, newLines, newLines.Count);
            if (expanded == null)
            {
                return null;
            }

            result = expanded;
        }

        return result;
    }

    /// <summary>
    /// <paramref name="hunks"/>, a diff's as git wrote it, expanded to show what
    /// <paramref name="shown"/>, the hunks of the diff it was before, as they were expanded,
    /// showed of the file: the file changed, the lines more of it shown still are. Lines are known
    /// by their number in the last commit, which saving the file doesn't move. What was shown of
    /// the file next to a hunk, above it or below, is shown next to it again, wherever the hunks
    /// are now; what was shown apart from any hunk now is not, as an expansion can't show it. Only
    /// lines an expansion showed count: what git showed of a hunk that is gone isn't expanded to.
    /// </summary>
    public static IReadOnlyList<DiffHunk> ExpandAsShown(IReadOnlyList<DiffHunk> hunks, IReadOnlyList<DiffHunk> shown, IReadOnlyList<string> newLines)
    {
        var before = new HashSet<int>();
        var expanded = new HashSet<int>();
        foreach (var line in shown.SelectMany(_ => _.Lines))
        {
            if (line.OldNumber is not { } number)
            {
                continue;
            }

            before.Add(number);
            if (line.Index == null)
            {
                expanded.Add(number);
            }
        }

        if (expanded.Count == 0)
        {
            return hunks;
        }

        bool AnyExpanded(int first, int count) =>
            Enumerable.Range(first, count).Any(expanded.Contains);

        // From the last gap between hunks to the first, so expanding one, or merging two, leaves
        // the hunks before it where they were.
        var result = hunks;
        for (var index = result.Count - 1; index >= 0; index--)
        {
            var hunk = result[index];

            // The lines between this hunk and the one before it, the bottom's its own, by their
            // numbers in the last commit.
            int gapStart;
            int gapEnd;
            if (hunk.IsBottom)
            {
                gapStart = hunk.Header.OldStart;
                gapEnd = hunk.Header.OldStart + hunk.Header.OldCount - 1;
            }
            else if (index == 0)
            {
                gapStart = 1;
                gapEnd = hunk.Header.OldStart - 1;
            }
            else
            {
                var previous = result[index - 1].Header;
                gapStart = previous.OldStart + previous.OldCount;
                gapEnd = hunk.Header.OldStart - 1;
            }

            // Above the hunk: none for the bottom, which shows only what is below the one before.
            var above = 0;
            if (!hunk.IsBottom)
            {
                while (gapEnd - above >= gapStart &&
                       before.Contains(gapEnd - above))
                {
                    above++;
                }

                // All of the gap shown merges the hunk into the one before.
                if (above > 0 &&
                    AnyExpanded(gapEnd - above + 1, above))
                {
                    result = Expand(result, index, up: true, newLines, above) ?? result;
                }
                else
                {
                    above = 0;
                }
            }

            // Below the hunk before, up to what was shown above this one.
            if (index == 0)
            {
                continue;
            }

            var below = 0;
            while (gapStart + below <= gapEnd - above &&
                   before.Contains(gapStart + below))
            {
                below++;
            }

            if (below > 0 &&
                AnyExpanded(gapStart, below))
            {
                result = Expand(result, index - 1, up: false, newLines, below) ?? result;
            }
        }

        return result;
    }

    static DiffHunk Merge(DiffHunk first, DiffHunk second)
    {
        var header = first.Header with
        {
            OldCount = first.Header.OldCount + second.Header.OldCount,
            NewCount = first.Header.NewCount + second.Header.NewCount
        };
        return new(header, header.ToString(), [.. first.Lines, .. second.Lines], first.Expansion);
    }

    /// <summary>
    /// The lines of a file's text, without their line endings, for expanding its diff.
    /// </summary>
    public static IReadOnlyList<string> LinesOf(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;

        // Text ending in a line ending has no line after it.
        if (count > 0 &&
            lines[^1].Length == 0)
        {
            count--;
        }

        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = lines[index].TrimEnd('\r');
        }

        return result;
    }
}
