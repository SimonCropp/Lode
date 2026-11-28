using System.Runtime.CompilerServices;

/// <summary>
/// The colours of a diff's code, in the language of the file it is of, as GitHub Desktop
/// colours them: each line's runs, from the state it starts in, which what goes on over lines,
/// a block comment, a verbatim string, a tag's attributes, leaves it in. A line of the file as it
/// is now, added or one both sides have, starts as the file has it there, given the file's
/// lines, every one of which is gone through for that off the UI thread, as the diff is laid
/// out. A deleted line, or one both have drawn as the old file's, side by side, starts as the
/// lines before it in its hunk leave it, the hunk's first as the file now has it where the hunk
/// starts. Shown more of the file, a hunk's lines start as the lines brought in above them leave
/// them. Without the file's lines, too big to expand say, where a hunk starts is a guess: in a
/// block comment when the first of opening one and closing one its lines do is closing it,
/// otherwise code; a hunk starting in a string that goes on over lines, or a tag's attributes,
/// starts wrongly in code. A line's runs are worked out when it is first drawn, and kept.
/// </summary>
sealed class DiffSyntax
{
    // How many lines' runs are kept, those of a good many screens; past it they are let go, to
    // be worked out again as they are drawn.
    const int keptLines = 4096;

    // How far into a hunk to look for the close of a block comment it starts in.
    const int guessedFrom = 200;

    // The state each line of the file as it is now starts in, from its first; after them, the
    // state the last ends in. Null without the file's lines.
    readonly int[]? fileStates;

    readonly Dictionary<DiffHunk, HunkStates> hunkStates = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<LineKey, SyntaxRun[]> runs = new(LineKeyComparer.Instance);
    readonly List<SyntaxRun> found = [];

    DiffSyntax(SyntaxLanguage language, IReadOnlyList<string>? lines)
    {
        Language = language;
        if (lines != null)
        {
            fileStates = StatesOf(language, lines);
        }
    }

    public SyntaxLanguage Language { get; }

    /// <summary>
    /// The colours of <paramref name="hunks"/>, a diff of the file at <paramref name="path"/>,
    /// whose lines are now <paramref name="lines"/> where they were read: where each of the file's
    /// lines and the hunks' starts, worked out here, off the UI thread. Null for a file of a
    /// language Lode has no colours for.
    /// </summary>
    public static DiffSyntax? Of(string path, IReadOnlyList<DiffHunk> hunks, IReadOnlyList<string>? lines)
    {
        if (SyntaxLanguage.For(path) is not { } language)
        {
            return null;
        }

        var syntax = new DiffSyntax(language, lines);
        foreach (var hunk in hunks)
        {
            syntax.States(hunk);
        }

        return syntax;
    }

    /// <summary>
    /// The state each of <paramref name="lines"/> starts in, the first in code, and, after them,
    /// the state the last ends in.
    /// </summary>
    public static int[] StatesOf(SyntaxLanguage language, IReadOnlyList<string> lines)
    {
        var states = new int[lines.Count + 1];
        var state = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            states[index] = state;
            state = language.Tokenize(lines[index], state, null);
        }

        states[^1] = state;
        return states;
    }

    /// <summary>
    /// The runs of <paramref name="line"/>, of <paramref name="hunk"/>, drawn as the old file's
    /// when <paramref name="old"/>, as a deleted line is, otherwise as the new file's.
    /// </summary>
    public SyntaxRun[] RunsOf(DiffHunk hunk, DiffLine line, bool old)
    {
        var key = new LineKey(hunk, line, old);
        if (runs.TryGetValue(key, out var known))
        {
            return known;
        }

        found.Clear();
        Language.Tokenize(line.ContentSpan, StateOf(hunk, line, old), found);
        SyntaxRun[] lineRuns = [.. found];
        if (runs.Count >= keptLines)
        {
            runs.Clear();
        }

        runs[key] = lineRuns;
        return lineRuns;
    }

    /// <summary>
    /// The state a line starts in, as drawn on a side. For tests.
    /// </summary>
    internal int StateOf(DiffHunk hunk, DiffLine line, bool old)
    {
        if (!old &&
            fileStates != null &&
            line.NewNumber is { } number &&
            number >= 1 &&
            number < fileStates.Length)
        {
            return fileStates[number - 1];
        }

        var states = States(hunk);
        SideStates? side = states.Old;
        if (!old)
        {
            side = states.New;
        }

        return side?.Of(NumberOn(line, old)) ?? 0;
    }

    static int? NumberOn(DiffLine line, bool old)
    {
        if (old)
        {
            return line.OldNumber;
        }

        return line.NewNumber;
    }

    // Where each of a hunk's lines starts, worked out the first time it is asked for: a hunk
    // shown more of is a hunk of its own.
    HunkStates States(DiffHunk hunk)
    {
        if (hunkStates.TryGetValue(hunk, out var known))
        {
            return known;
        }

        SideStates? newSide = null;
        if (fileStates == null)
        {
            newSide = Carried(hunk, old: false);
        }

        var states = new HunkStates(Carried(hunk, old: true), newSide);
        hunkStates[hunk] = states;
        return states;
    }

    // The state each of a hunk's lines on a side starts in, as the lines before it leave it, the
    // first as the hunk is taken to start. A side's lines are numbered one after another.
    SideStates Carried(DiffHunk hunk, bool old)
    {
        var first = 0;
        var count = 0;
        foreach (var line in hunk.Lines)
        {
            if (NumberOn(line, old) is { } number)
            {
                if (count == 0)
                {
                    first = number;
                }

                count++;
            }
        }

        var states = new int[count];
        var state = StartOf(hunk, old);
        var index = 0;
        foreach (var line in hunk.Lines)
        {
            if (NumberOn(line, old) == null)
            {
                continue;
            }

            states[index++] = state;
            state = Language.Tokenize(line.ContentSpan, state, null);
        }

        return new(first, states);
    }

    // Where a hunk's first line on a side starts: as the file now has it where the hunk starts,
    // given its lines, which is where the old file had it too, unless a change above went into or
    // out of a comment; otherwise a guess.
    int StartOf(DiffHunk hunk, bool old)
    {
        var at = hunk.Header.NewStart - 1;
        if (fileStates != null &&
            at >= 0 &&
            at < fileStates.Length)
        {
            return fileStates[at];
        }

        return Guess(hunk, old);
    }

    // In a block comment when the first of opening one and closing one that the hunk's lines on
    // the side do is closing it; otherwise code.
    int Guess(DiffHunk hunk, bool old)
    {
        if (Language.BlockComment is not { } comment)
        {
            return 0;
        }

        var looked = 0;
        foreach (var line in hunk.Lines)
        {
            if (NumberOn(line, old) == null)
            {
                continue;
            }

            if (looked++ == guessedFrom)
            {
                break;
            }

            var content = line.ContentSpan;
            var open = content.IndexOf(comment.Open, StringComparison.Ordinal);
            var close = content.IndexOf(comment.Close, StringComparison.Ordinal);
            if (close >= 0 &&
                (open < 0 || close < open))
            {
                return comment.State;
            }

            if (open >= 0)
            {
                return 0;
            }
        }

        return 0;
    }

    // The states of a side's lines, by their number there, from the first's.
    readonly record struct SideStates(int First, int[] States)
    {
        public int? Of(int? number)
        {
            if (number is not { } at ||
                at - First < 0 ||
                at - First >= States.Length)
            {
                return null;
            }

            return States[at - First];
        }
    }

    // The new side's are the file's, given its lines.
    sealed record HunkStates(SideStates Old, SideStates? New);

    readonly record struct LineKey(DiffHunk Hunk, DiffLine Line, bool Old);

    // A line by which it is, not what it says, which would hash all its text every time it is
    // drawn: the same line in another hunk, shown more of, can start otherwise.
    sealed class LineKeyComparer : IEqualityComparer<LineKey>
    {
        public static LineKeyComparer Instance { get; } = new();

        public bool Equals(LineKey first, LineKey second) =>
            ReferenceEquals(first.Hunk, second.Hunk) &&
            ReferenceEquals(first.Line, second.Line) &&
            first.Old == second.Old;

        public int GetHashCode(LineKey key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Hunk), RuntimeHelpers.GetHashCode(key.Line), key.Old);
    }
}
