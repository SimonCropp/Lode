using System.Collections.Immutable;

/// <summary>
/// Which of a file's added and deleted lines go in the next commit, as GitHub Desktop's
/// DiffSelection keeps them: all or none, but for those picked otherwise, each by the
/// <see cref="DiffLine.Index"/> it has in the diff git wrote, whose hash they are kept with.
/// <paramref name="Count"/> is how many lines of the diff can be picked.
/// </summary>
sealed record LinePicks(bool AllByDefault, ImmutableHashSet<int> Diverging, int Count, string PatchHash)
{
    public static LinePicks All(TextDiff diff) =>
        new(true, [], Pickable(diff).Count(), diff.PatchHash);

    /// <summary>
    /// The lines of <paramref name="diff"/> that can be picked: those added or deleted.
    /// </summary>
    public static IEnumerable<int> Pickable(TextDiff diff) =>
        diff.Hunks
            .SelectMany(_ => _.Lines)
            .Where(_ => _ is { IsChange: true, Index: not null })
            .Select(_ => _.Index!.Value);

    public bool IsPicked(int index) =>
        AllByDefault != Diverging.Contains(index);

    public int PickedCount
    {
        get
        {
            if (AllByDefault)
            {
                return Count - Diverging.Count;
            }

            return Diverging.Count;
        }
    }

    /// <summary>
    /// True when every line is picked, false when none is, null for some: how the file's tick
    /// shows.
    /// </summary>
    public bool? State
    {
        get
        {
            var picked = PickedCount;
            if (picked == Count)
            {
                return true;
            }

            if (picked == 0)
            {
                return false;
            }

            return null;
        }
    }

    /// <summary>
    /// These picks with <paramref name="indexes"/> picked, or not, every line picked alike kept
    /// as all or none.
    /// </summary>
    public LinePicks With(IEnumerable<int> indexes, bool picked)
    {
        var diverging = Diverging.ToBuilder();
        foreach (var index in indexes)
        {
            if (picked == AllByDefault)
            {
                diverging.Remove(index);
            }
            else
            {
                diverging.Add(index);
            }
        }

        if (diverging.Count == Count)
        {
            return this with
            {
                AllByDefault = !AllByDefault,
                Diverging = []
            };
        }

        return this with
        {
            Diverging = diverging.ToImmutable()
        };
    }
}

/// <summary>
/// The patch of the lines picked in a file's diff, for git apply --cached to put in the index a
/// commit is made from, as GitHub Desktop's formatPatch in patch-formatter.ts writes it: a line
/// added and picked is added, one not is left out, as if never added; a line deleted and picked
/// is deleted, one not is kept, as context. A hunk with nothing picked is left out. A last line
/// that had no newline, kept, gains one when lines picked are added after it, as they can't
/// follow it otherwise.
/// </summary>
static class LinePatch
{
    /// <summary>
    /// The patch of the lines <paramref name="picks"/> says in <paramref name="diff"/>, the diff
    /// git wrote. Null when none is picked. A file deleted isn't deleted by the patch, only the
    /// lines picked, as some aren't.
    /// </summary>
    public static string? Format(TextDiff diff, LinePicks picks, bool deleted)
    {
        var body = new StringBuilder();
        var shift = 0;
        foreach (var (header, _, hunkLines, _) in diff.Hunks)
        {
            var lines = new StringBuilder();
            var oldCount = 0;
            var newCount = 0;
            var anyChange = false;
            for (var i = 0; i < hunkLines.Count; i++)
            {
                var line = hunkLines[i];
                if (line.Kind == DiffLineKind.Context)
                {
                    lines.Append(line.Text).Append('\n');
                    oldCount++;
                    newCount++;
                }
                else if (line.Index is { } index &&
                         picks.IsPicked(index))
                {
                    lines.Append(line.Text).Append('\n');
                    if (line.Kind == DiffLineKind.Add)
                    {
                        newCount++;
                    }
                    else
                    {
                        oldCount++;
                    }

                    anyChange = true;
                }
                else if (line.Kind == DiffLineKind.Add)
                {
                    // Never added, as far as this patch goes.
                    continue;
                }
                else if (line.NoTrailingNewline &&
                         PickedAddedAfter(hunkLines, i, picks))
                {
                    // The last line, which had no newline, kept, with lines picked added after
                    // it: as context, still with no newline, git would join it to the first of
                    // them. Taken away and added back with a newline instead, as the lines
                    // picked can't follow it without one.
                    lines.Append(line.Text).Append('\n');
                    lines.Append("\\ No newline at end of file\n");
                    lines.Append('+').Append(line.Text, 1, line.Text.Length - 1).Append('\n');
                    oldCount++;
                    newCount++;
                    continue;
                }
                else
                {
                    // Never deleted: still there, as context.
                    lines.Append(' ').Append(line.Text, 1, line.Text.Length - 1).Append('\n');
                    oldCount++;
                    newCount++;
                }

                if (line.NoTrailingNewline)
                {
                    lines.Append("\\ No newline at end of file\n");
                }
            }

            if (!anyChange)
            {
                continue;
            }

            // A side with no lines in the hunk names the line before it; one with some, its
            // first. On the new side, moved by what the hunks before added and took away.
            var newStart = header.OldStart + shift;
            if (oldCount == 0)
            {
                newStart++;
            }

            if (newCount == 0)
            {
                newStart--;
            }

            body.Append(CultureInfo.InvariantCulture, $"@@ -{header.OldStart},{oldCount} +{newStart},{newCount} @@\n");
            body.Append(lines);
            shift += newCount - oldCount;
        }

        if (body.Length == 0)
        {
            return null;
        }

        return Header(diff.Patch, deleted) + body;
    }

    // Whether any line added after the one at position in the hunk is picked.
    static bool PickedAddedAfter(IReadOnlyList<DiffLine> hunkLines, int position, LinePicks picks) =>
        hunkLines
            .Skip(position + 1)
            .Any(_ => _ is { Kind: DiffLineKind.Add, Index: { } index } && picks.IsPicked(index));

    // The lines git wrote above the first hunk, which name the file as git would have them
    // named. A deleted file's say it is modified instead, as only some of its lines go.
    static string Header(string patch, bool deleted)
    {
        var header = new StringBuilder();
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                break;
            }

            if (!deleted)
            {
                header.Append(line).Append('\n');
                continue;
            }

            if (line.StartsWith("deleted file mode", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                continue;
            }

            header.Append(line).Append('\n');
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                header.Append("+++ ").Append(NewSide(line[4..])).Append('\n');
            }
        }

        return header.ToString();
    }

    // The new side's name of a file named on the old side: a/ for b/, inside git's quotes when
    // the name is quoted.
    static string NewSide(string oldName)
    {
        if (oldName.StartsWith("\"a/", StringComparison.Ordinal))
        {
            return string.Concat("\"b/", oldName.AsSpan(3));
        }

        if (oldName.StartsWith("a/", StringComparison.Ordinal))
        {
            return string.Concat("b/", oldName.AsSpan(2));
        }

        return oldName;
    }
}
