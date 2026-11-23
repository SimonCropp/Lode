/// <summary>
/// A diff to draw: a 160 line file, a line added in one hunk and a line replaced by an if in
/// another, as git writes it, with the file's lines as they are now, for the arrows.
/// </summary>
static class DiffSample
{
    public const string Repo = @"C:\Code\Sample";

    public static Change File { get; } = new("src/Scripting/ScriptRunner.cs", null, GitChange.Modified);

    static string[] firstHunk =
    [
        "@@ -118,6 +118,7 @@ sealed class ScriptRunner",
        "     {",
        "         var options = BuildOptions();",
        "         var start = document.Reader.Position;",
        "+        var written = document.Written;",
        "         var previous = document.Current;",
        " ",
        "         // Run the script where the parser stopped."
    ];

    static string[] secondHunk =
    [
        "@@ -137,7 +138,13 @@ sealed class ScriptRunner",
        "             document.Current = previous;",
        "         }",
        " ",
        "-        document.Reader.Position = start;",
        "+        // The parser goes on with what the script wrote; it may have gone on meanwhile",
        "+        // (an async script), so its place only changes if the script wrote.",
        "+        if (document.Written != written)",
        "+        {",
        "+            document.Reader.Position = start;",
        "+        }",
        "+",
        "         await document.Queue(AfterScript).ConfigureAwait(false);",
        "         await document.Queue(Loaded).ConfigureAwait(false);",
        "         response.Dispose();"
    ];

    public static string Patch { get; } = string.Join(
        '\n',
        [
            "diff --git a/src/Scripting/ScriptRunner.cs b/src/Scripting/ScriptRunner.cs",
            "index 1111111..2222222 100644",
            "--- a/src/Scripting/ScriptRunner.cs",
            "+++ b/src/Scripting/ScriptRunner.cs",
            .. firstHunk,
            .. secondHunk,
            ""
        ]);

    public static TextDiff Diff => (TextDiff) DiffParser.Parse(Patch);

    /// <summary>
    /// The file now: a step on each line but those the hunks show.
    /// </summary>
    static IReadOnlyList<string> NewLines
    {
        get
        {
            var lines = Enumerable.Range(1, 160)
                .Select(_ => $"        Step{_}();")
                .ToArray();
            foreach (var hunk in Diff.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.NewNumber is { } number)
                    {
                        lines[number - 1] = line.Content;
                    }
                }
            }

            return lines;
        }
    }

    /// <summary>
    /// Answers for git with the sample, or <paramref name="diff"/>, until disposed.
    /// </summary>
    public static IDisposable Stub(FileDiff? diff = null, IReadOnlyList<string>? newLines = null)
    {
        var read = DiffView.Read;
        var readLines = DiffView.ReadNewLines;
        var shown = diff ?? Diff;
        var lines = newLines ?? NewLines;
        DiffView.Read = (_, _, _, _) => shown;
        DiffView.ReadNewLines = _ => lines;
        return new Restore(
            () =>
            {
                DiffView.Read = read;
                DiffView.ReadNewLines = readLines;
            });
    }

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() =>
            restore();
    }
}
