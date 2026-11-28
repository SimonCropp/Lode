/// <summary>
/// What a line of a diff is: one both sides have, one added, or one deleted. A hunk's @@ line
/// is its <see cref="DiffHunk.Heading"/>, not a line of it.
/// </summary>
enum DiffLineKind
{
    Context,
    Add,
    Delete
}

/// <summary>
/// A line of a diff as git wrote it, its +, - or space first, with the line it is on each side:
/// an added line is on the new side only, a deleted one on the old side only. <paramref name="Index"/>
/// counts the added, deleted and context lines of the diff git wrote, from 0, so a line keeps it
/// however the hunks are expanded; lines an expansion brought in have none, so are never picked
/// to commit. As GitHub Desktop's DiffLine.
/// </summary>
sealed record DiffLine(DiffLineKind Kind, string Text, int? Index, int? OldNumber, int? NewNumber, bool NoTrailingNewline = false)
{
    // A line made from another with other text, by with, works its content out again.
    public string Text
    {
        get;
        init
        {
            field = value;
            content = null;
        }
    } = Text;

    // Worked out the first time it is asked for, rather than cut from the text every time, as it
    // is asked for whenever the line is drawn or the pointer moves over it. The lines of a big
    // diff never drawn never have one made.
    string? content;

    /// <summary>
    /// The line as the file has it: without the +, - or space git puts first, nor the carriage
    /// return git leaves at the end of a line of a file with Windows line endings.
    /// </summary>
    public string Content => content ??= ContentSpan.ToString();

    /// <summary>
    /// <see cref="Content"/> as the part of <see cref="Text"/> it is, for what reads every line
    /// of a diff, to measure it, to make no string of each.
    /// </summary>
    public CharSpan ContentSpan
    {
        get
        {
            var end = Text.Length;
            if (end > 1 &&
                Text[end - 1] == '\r')
            {
                end--;
            }

            if (end <= 1)
            {
                return [];
            }

            return Text.AsSpan(1, end - 1);
        }
    }

    public bool IsChange => Kind is DiffLineKind.Add or DiffLineKind.Delete;

    // Whether its content has been worked out yet is no part of what a line is.
    public bool Equals(DiffLine? other) =>
        other is not null &&
        Kind == other.Kind &&
        Text == other.Text &&
        Index == other.Index &&
        OldNumber == other.OldNumber &&
        NewNumber == other.NewNumber &&
        NoTrailingNewline == other.NoTrailingNewline;

    public override int GetHashCode() =>
        HashCode.Combine(Kind, Text, Index, OldNumber, NewNumber, NoTrailingNewline);
}

/// <summary>
/// Where a hunk is in each side of the file, as its @@ line says: the line it starts on and how
/// many it covers.
/// </summary>
sealed record HunkHeader(int OldStart, int OldCount, int NewStart, int NewCount)
{
    public override string ToString() =>
        $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@";
}

/// <summary>
/// Which arrows a hunk's heading has to show more of the file: none at the top of the file; up
/// above the first hunk; down, on the last, a hunk of no lines GitHub Desktop adds below the last
/// to show the rest of the file; both between hunks far apart; one to show all of a short gap.
/// </summary>
enum HunkExpansion
{
    None,
    Up,
    Down,
    Both,
    Short
}

/// <summary>
/// A hunk: its header, the @@ line it was written as, its lines, and what can be expanded above
/// it. One with no lines is the one added below the last to show the rest of the file.
/// </summary>
sealed record DiffHunk(HunkHeader Header, string Heading, IReadOnlyList<DiffLine> Lines, HunkExpansion Expansion)
{
    public bool IsBottom => Lines.Count == 0;
}

/// <summary>
/// What a file's diff is, as GitHub Desktop sorts them: lines of text, a binary file, an image,
/// text too big to show without being asked, or too big to show at all, or nothing to show.
/// </summary>
abstract record FileDiff;

/// <summary>
/// The hunks of a text file's diff, with the text git wrote, which is what lines picked to commit
/// are kept against: they are let go once the file's diff is not that any more.
/// </summary>
sealed record TextDiff(IReadOnlyList<DiffHunk> Hunks, string Patch) : FileDiff
{
    // A diff made from another with other text, by with, works its hash out again.
    public string Patch
    {
        get;
        init
        {
            field = value;
            patchHash = null;
        }
    } = Patch;

    // Worked out the first time it is asked for, rather than the whole patch hashed every time
    // lines picked are matched to it.
    string? patchHash;

    public int Added => Hunks.Sum(_ => _.Lines.Count(_ => _.Kind == DiffLineKind.Add));

    public int Deleted => Hunks.Sum(_ => _.Lines.Count(_ => _.Kind == DiffLineKind.Delete));

    /// <summary>
    /// The highest line number on either side, for how wide the numbers are.
    /// </summary>
    public int MaxLineNumber
    {
        get
        {
            var max = 0;
            foreach (var hunk in Hunks)
            {
                max = Math.Max(max, hunk.Header.OldStart + hunk.Header.OldCount);
                max = Math.Max(max, hunk.Header.NewStart + hunk.Header.NewCount);
            }

            return max;
        }
    }

    /// <summary>
    /// A short hash of <see cref="Patch"/>, which lines picked to commit are noted against.
    /// </summary>
    public string PatchHash => patchHash ??= HashOf(Patch);

    /// <summary>
    /// The <see cref="PatchHash"/> of a diff whose text is <paramref name="patch"/>.
    /// </summary>
    public static string HashOf(string patch) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patch)))[..16];

    // Whether its hash has been worked out yet is no part of what a diff is.
    public bool Equals(TextDiff? other) =>
        other is not null &&
        base.Equals(other) &&
        EqualityComparer<IReadOnlyList<DiffHunk>>.Default.Equals(Hunks, other.Hunks) &&
        Patch == other.Patch;

    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), Hunks, Patch);
}

sealed record BinaryDiff : FileDiff;

/// <summary>
/// An image as it was and as it is, either missing for one added or deleted.
/// </summary>
sealed record ImageDiff(byte[]? Old, byte[]? New) : FileDiff;

/// <summary>
/// A diff too big to show straight away, which is shown when asked, as GitHub Desktop's Show
/// diff button does.
/// </summary>
sealed record LargeDiff(string Patch) : FileDiff;

/// <summary>
/// A diff too big to show at all.
/// </summary>
sealed record UnrenderableDiff : FileDiff;

/// <summary>
/// A diff with nothing in it to show: an empty file, a rename alone, a change of mode, or changes
/// only to whitespace when whitespace is hidden.
/// </summary>
sealed record EmptyDiff : FileDiff;

/// <summary>
/// A diff git couldn't write, with what it said.
/// </summary>
sealed record FailedDiff(string Error) : FileDiff;
