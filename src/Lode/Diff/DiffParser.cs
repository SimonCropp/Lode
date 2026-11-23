/// <summary>
/// Reads the diff git writes for a file into hunks, as GitHub Desktop's diff-parser.ts does:
/// what comes before the +++ line is the header; a "Binary files … differ" line there says the
/// file is binary; each hunk follows its @@ line, the lines numbered on each side from where it
/// says the hunk starts. A "\ No newline at end of file" marks the line before it rather than
/// being a line itself.
/// </summary>
static partial class DiffParser
{
    /// <summary>
    /// Text with a line longer than this is shown only when asked, as GitHub Desktop does.
    /// </summary>
    public const int MaxLineLength = 5000;

    /// <summary>
    /// Text bigger than this, in characters, is shown only when asked: GitHub Desktop's
    /// MaxReasonableDiffSize, 4.4 MB.
    /// </summary>
    const int maxReasonableLength = 70 * 1024 * 1024 / 16;

    /// <summary>
    /// Text bigger than this is not shown at all: GitHub Desktop's MaxDiffBufferSize, 70 MB.
    /// </summary>
    const int maxLength = 70 * 1024 * 1024;

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    /// <summary>
    /// The diff of what git wrote, which is too big to show unless <paramref name="evenIfLarge"/>.
    /// </summary>
    public static FileDiff Parse(string patch, bool evenIfLarge = false)
    {
        if (patch.Length > maxLength)
        {
            return new UnrenderableDiff();
        }

        if (!evenIfLarge &&
            IsLarge(patch))
        {
            return new LargeDiff(patch);
        }

        var lines = patch.Split('\n');
        var index = 0;
        var inHeader = true;
        while (inHeader &&
               index < lines.Length)
        {
            var line = lines[index];
            var binary = line.StartsWith("Binary files ", StringComparison.Ordinal) &&
                         line.TrimEnd('\r').EndsWith(" differ", StringComparison.Ordinal);
            if (binary ||
                line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                return new BinaryDiff();
            }

            if (line.StartsWith("+++", StringComparison.Ordinal))
            {
                inHeader = false;
            }

            index++;
        }

        var hunks = new List<DiffHunk>();
        var lineIndex = 0;
        while (index < lines.Length &&
               lines[index].StartsWith("@@", StringComparison.Ordinal))
        {
            var heading = lines[index];
            var match = HunkHeaderRegex().Match(heading);
            if (!match.Success)
            {
                break;
            }

            var header = new HunkHeader(
                Number(match, 1, 1),
                Number(match, 2, 1),
                Number(match, 3, 1),
                Number(match, 4, 1));
            index++;
            var old = header.OldStart;
            var @new = header.NewStart;
            var hunkLines = new List<DiffLine>();
            while (index < lines.Length &&
                   lines[index].Length > 0)
            {
                var line = lines[index];
                var first = line[0];
                if (first == '\\')
                {
                    if (hunkLines.Count > 0)
                    {
                        hunkLines[^1] = hunkLines[^1] with
                        {
                            NoTrailingNewline = true
                        };
                    }

                    index++;
                    continue;
                }

                DiffLine? read = first switch
                {
                    '+' => new(DiffLineKind.Add, line, lineIndex, null, @new++),
                    '-' => new(DiffLineKind.Delete, line, lineIndex, old++, null),
                    ' ' => new(DiffLineKind.Context, line, lineIndex, old++, @new++),
                    _ => null
                };
                if (read == null)
                {
                    break;
                }

                hunkLines.Add(read);
                lineIndex++;
                index++;
            }

            if (hunkLines.Count == 0)
            {
                break;
            }

            DiffHunk? previous = null;
            if (hunks.Count > 0)
            {
                previous = hunks[^1];
            }

            hunks.Add(new(header, heading.TrimEnd('\r'), hunkLines, DiffExpansion.ExpansionOf(hunks.Count, header, previous)));
        }

        if (hunks.Count == 0)
        {
            return new EmptyDiff();
        }

        return new TextDiff(hunks, patch);
    }

    // A line longer than GitHub Desktop shows straight away, or more text than it does.
    static bool IsLarge(string patch)
    {
        if (patch.Length > maxReasonableLength)
        {
            return true;
        }

        var start = 0;
        while (start < patch.Length)
        {
            var end = patch.IndexOf('\n', start);
            if (end < 0)
            {
                end = patch.Length;
            }

            if (end - start > MaxLineLength)
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    // A count git leaves out is 1.
    static int Number(Match match, int group, int missing)
    {
        var value = match.Groups[group];
        if (!value.Success)
        {
            return missing;
        }

        return int.Parse(value.ValueSpan, CultureInfo.InvariantCulture);
    }
}
