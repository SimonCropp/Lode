/// <summary>
/// Answers for git while the Changes and History panes are tested in a window: a repository with
/// a few changed files and a short history, read at once, with no origin, and panes that slide
/// out without sliding. Puts back what was there when disposed.
/// </summary>
sealed class PaneStubs : IDisposable
{
    public static DateTimeOffset Now { get; } = new(2025, 4, 1, 12, 0, 0, TimeSpan.Zero);

    GitState.Reader readGit = FolderView.ReadGit;
    Func<string, Cancel, GitChanges> readChanges = ChangesPane.Read;
    Func<string, int, int, HistoryWalk, Cancel, List<GitCommit>?> readCommits = HistoryPane.ReadCommits;
    Func<string, GitCommit, Cancel, List<Change>?> readFiles = HistoryPane.ReadFiles;
    Func<string, Cancel, HashSet<string>> readUnpushed = HistoryPane.ReadUnpushed;
    Func<string, Cancel, bool> readNeverPushed = HistoryPane.ReadNeverPushed;
    Func<string, string, int, int, Cancel, List<GitCommit>?> readRange = HistoryPane.ReadRange;
    Func<string, string, Cancel, (int Ahead, int Behind)?> readApart = HistoryPane.ReadApart;
    TimeProvider time = HistoryPane.Time;
    TimeSpan slide = GitPanes.SlideDuration;

    public static IReadOnlyList<Change> Files { get; } =
    [
        new("Docs/guide.aleph", null, GitChange.Modified),
        new("notes.aleph", null, GitChange.Deleted),
        new("report.beta", "report2.beta", GitChange.Renamed),
        new("Source/Deep/leaf.aleph", null, GitChange.Modified),
        new("Source/Deep/new.aleph", null, GitChange.Untracked),
        new("Source/main.aleph", null, GitChange.Modified)
    ];

    /// <summary>
    /// Feature merged into main, left part way by conflicts: one a side deleted, one with two
    /// conflicts marked, one settled in its text, and a file the merge brought in cleanly.
    /// </summary>
    public static GitChanges Conflicted { get; } = new(
        "main",
        true,
        true,
        [
            new("Docs/guide.aleph", null, GitChange.Conflicted, new(0, true, TheirsDeleted: true)),
            new("Source/main.aleph", null, GitChange.Conflicted, new(2, false)),
            new("notes.aleph", null, GitChange.Modified),
            new("Source/Deep/leaf.aleph", null, GitChange.Conflicted, new(0, false))
        ],
        "feature",
        "Merge branch 'feature'");

    public static List<GitCommit> History { get; } =
    [
        Commit("e5f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9", "Rename several items at once", "Ada Lovelace", TimeSpan.FromHours(2)),
        Commit("d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3", "Sort large folders off the UI thread", "Ada Lovelace", TimeSpan.FromHours(7), "0.25.0"),
        Commit("c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2", "Wait less on git, the disk and the clipboard", "Grace Hopper", TimeSpan.FromHours(9)),
        Commit("b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1", "Fix the smaller bugs the review found", "Alan Turing", TimeSpan.FromDays(1)),
        Commit("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0", "Update readme.md", "Grace Hopper", TimeSpan.FromDays(3), "0.24.0", "stable"),
        Commit("f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9", "Initial commit", "Alan Turing", TimeSpan.FromDays(40))
    ];

    public static List<Change> CommitFiles { get; } =
    [
        new("src/Lode/FolderList/FolderView.cs", null, GitChange.Modified),
        new("src/Lode/Window/MainForm.cs", null, GitChange.Modified),
        new("src/Lode/FileActions/BulkRename.cs", null, GitChange.Added),
        new("src/Lode.Tests/Window/MainFormTests.cs", null, GitChange.Modified)
    ];

    static GitCommit Commit(string sha, string subject, string author, TimeSpan ago, params string[] tags) =>
        new(sha, ["0000000000000000000000000000000000000000"], author, Now - ago, tags, subject);

    public PaneStubs()
    {
        FolderView.ReadGit = (_, _) => new("main", History[0].Sha, 1, 0, Files.Select(_ => new GitFile(_.Path, _.Kind)).ToList())
        {
            UpstreamName = "origin/main"
        };
        ChangesPane.Read = (_, _) => new("main", true, false, Files);
        HistoryPane.ReadCommits = (_, skip, count, _, _) => History.Skip(skip).Take(count).ToList();
        HistoryPane.ReadFiles = (_, _, _) => CommitFiles;
        HistoryPane.ReadUnpushed = (_, _) => [History[0].Sha];
        HistoryPane.ReadNeverPushed = (_, _) => false;
        HistoryPane.ReadRange = (_, _, _, _, _) => [];
        HistoryPane.ReadApart = (_, _, _) => (0, 0);
        HistoryPane.Time = new Clock(Now);
        GitPanes.SlideDuration = TimeSpan.Zero;
    }

    public void Dispose()
    {
        FolderView.ReadGit = readGit;
        ChangesPane.Read = readChanges;
        HistoryPane.ReadCommits = readCommits;
        HistoryPane.ReadFiles = readFiles;
        HistoryPane.ReadUnpushed = readUnpushed;
        HistoryPane.ReadNeverPushed = readNeverPushed;
        HistoryPane.ReadRange = readRange;
        HistoryPane.ReadApart = readApart;
        HistoryPane.Time = time;
        GitPanes.SlideDuration = slide;
        GitCache.Reset();
    }

    /// <summary>
    /// A fixture that is a repository, and the code folder.
    /// </summary>
    public static Settings InRepository(Fixture fixture)
    {
        var git = fixture.At(".git");
        Directory.CreateDirectory(git);
        File.SetAttributes(git, FileAttributes.Directory | FileAttributes.Hidden);
        return new()
        {
            CodeDirectory = fixture.Root,
            ShowHidden = false
        };
    }

    /// <summary>
    /// Waits for the window to know its repository, and for the panes showing to have read it.
    /// </summary>
    public static async Task Settled(MainForm form)
    {
        // Pumped, not awaited: what they read is taken up on the window's thread, by its
        // messages.
        await Pump.Loaded(form.ActiveView!);
        await Pump.Until(() => form.ActiveView!.GitReading.IsCompleted);
        await Pump.Until(() => form.GitPanes.Repo != null);
        var panes = form.GitPanes;
        await Pump.Until(() => panes.ChangesPane.Reading.IsCompleted && panes.HistoryPane.Reading.IsCompleted && panes.HistoryPane.ReadingFiles.IsCompleted);
    }
}
