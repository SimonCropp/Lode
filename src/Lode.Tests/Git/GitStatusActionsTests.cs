using static CodeCommandsTests;

/// <summary>
/// What clicking the status bar's changes, ahead or behind asks, and the git it then runs, for
/// real, against throwaway repositories in the temp folder.
/// </summary>
public class GitStatusActionsTests
{
    const string commit = "0123456789abcdef0123456789abcdef01234567";

    static GitState State(int ahead = 0, int behind = 0, int modified = 0, int untracked = 0, string? branch = "main")
    {
        var files = Enumerable.Range(0, modified)
            .Select(_ => new GitFile($"src/{_}.cs", GitChange.Modified))
            .Concat(Enumerable.Range(0, untracked).Select(_ => new GitFile($"new{_}.txt", GitChange.Untracked)))
            .ToList();
        return new(branch, commit, ahead, behind, files)
        {
            Upstream = branch != null
        };
    }

    // A question as the tests show it: its heading and line, then each way to act, the first
    // being the default, with its note, then the files it is about.
    static string Describe(GitStatusActions.Question? question)
    {
        if (question == null)
        {
            return "(nothing to ask)";
        }

        var lines = new List<string>
        {
            $"[{question.Caption}] {question.Heading}"
        };
        if (question.Text.Length > 0)
        {
            lines.Add(question.Text);
        }

        lines.AddRange(question.Options.Select(_ => $"  -> {_.Text}: {_.Note}"));
        if (question.Details != null)
        {
            lines.Add($"  files: {question.Details.Replace("\n", "; ")}");
        }

        return string.Join('\n', lines);
    }

    static string Ask(GitState.PartKind part, GitState state) =>
        Describe(GitStatusActions.QuestionFor(part, state, "origin/main"));

    [Test]
    public Task Questions() =>
        Verify(
            new
            {
                behind = Ask(GitState.PartKind.Behind, State(behind: 14)),
                behindWithChanges = Ask(GitState.PartKind.Behind, State(behind: 1, modified: 2)),
                divergedBehind = Ask(GitState.PartKind.Behind, State(ahead: 2, behind: 14)),
                ahead = Ask(GitState.PartKind.Ahead, State(ahead: 1)),
                aheadWithChanges = Ask(GitState.PartKind.Ahead, State(ahead: 3, modified: 1)),
                divergedAhead = Ask(GitState.PartKind.Ahead, State(ahead: 2, behind: 14)),
                changes = Ask(GitState.PartKind.Changes, State(modified: 2, untracked: 1)),
                // Push adds new files too, so there is something to commit.
                untrackedOnly = Ask(GitState.PartKind.Changes, State(untracked: 2)),
                trackedOnly = Ask(GitState.PartKind.Changes, State(modified: 1)),
                // Nowhere to push from a detached HEAD.
                detached = Ask(GitState.PartKind.Changes, State(modified: 1, branch: null)),
                noCommits = Ask(GitState.PartKind.NoCommits, new("main", null, 0, 0, [])),
                nothingBehind = Ask(GitState.PartKind.Behind, State()),
                forkBehind = AskInFork(GitState.PartKind.ForkBehind, State() with { ForkBehind = 12 }),
                forkBehindWithChanges = AskInFork(GitState.PartKind.ForkBehind, State(modified: 1) with { ForkBehind = 1 }),
                // No branch to bring upstream's commits into.
                forkBehindDetached = AskInFork(GitState.PartKind.ForkBehind, State(branch: null) with { ForkBehind = 3 }),
                // A fork's fetch learns what upstream has too.
                behindInAFork = AskInFork(GitState.PartKind.Behind, State(behind: 2)),
                nothingBehindUpstream = AskInFork(GitState.PartKind.ForkBehind, State())
            });

    static string AskInFork(GitState.PartKind part, GitState state) =>
        Describe(GitStatusActions.QuestionFor(part, state, "origin/main", "upstream/main"));

    // A screenful, then how many more.
    [Test]
    public async Task FileListStopsAtAScreenful()
    {
        var list = GitStatusActions.FileList(State(modified: 32).Files);

        await Assert.That(list.Split('\n').Length).IsEqualTo(31);
        await Assert.That(list).StartsWith("Modified: src/0.cs\n");
        await Assert.That(list).EndsWith("\nand 2 more");
    }

    // A clone that another clone pushes past, so it is behind once it knows.
    static string CloneBehind(Scratch code)
    {
        code.CreateRepo("Repo");
        var pusher = code.Clone("Repo", "Pusher");
        var behind = code.Clone("Repo", "Behind");
        File.WriteAllText(Path.Combine(pusher, "tracked.txt"), "pushed");
        Scratch.Git(pusher, "commit", "-am", "Pushed");
        Scratch.Git(pusher, "push");
        return behind;
    }

    // Behind only counts what the last fetch learned: fetching finds the commit pushed meanwhile,
    // changing nothing checked out.
    [Test]
    public async Task FetchFindsWhatIsBehind()
    {
        using var code = new Scratch();
        var behind = CloneBehind(code);

        await Assert.That(GitState.FromGit(behind, Cancel.None)!.Behind).IsEqualTo(0);
        var result = (await CodeCommands.Fetch([behind])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(GitState.FromGit(behind, Cancel.None)!.Behind).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(behind, "tracked.txt"))).IsEqualTo("original");
    }

    // Push sends the commits only: a change not committed stays where it is.
    [Test]
    public async Task PushSendsTheCommitsOnly()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "committed");
        Scratch.Git(repo, "commit", "-am", "Committed");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "not committed");

        var result = (await CodeCommands.PushCommits([repo])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(GitState.FromGit(repo, Cancel.None)!.Ahead).IsEqualTo(0);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s", "main")).IsEqualTo("Committed");
        await Assert.That(GitState.FromGit(repo, Cancel.None)!.Changes).IsEqualTo(1);
    }

    // Diverged, origin refuses a push until its commits are brought in: pull, then push, sends
    // both ways.
    [Test]
    public async Task PullThenPushWhenDiverged()
    {
        using var code = new Scratch();
        var behind = CloneBehind(code);
        Scratch.Git(behind, "config", "pull.rebase", "true");
        await File.WriteAllTextAsync(Path.Combine(behind, "other.txt"), "other");
        Scratch.Git(behind, "add", "other.txt");
        Scratch.Git(behind, "commit", "-m", "Other");
        Scratch.Git(behind, "fetch");
        var before = GitState.FromGit(behind, Cancel.None)!;

        var result = (await CodeCommands.PullThenPush([behind])).Single();

        await Assert.That((before.Ahead, before.Behind)).IsEqualTo((1, 1));
        await Assert.That(result.Succeeded).IsTrue();
        var after = GitState.FromGit(behind, Cancel.None)!;
        await Assert.That((after.Ahead, after.Behind)).IsEqualTo((0, 0));
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "--format=%s", "main")).IsEqualTo("Other\nPushed\nInitial");
    }

    // A pull that fails pushes nothing.
    [Test]
    public async Task PullThenPushStopsWhenThePullFails()
    {
        using var code = new Scratch();
        var behind = CloneBehind(code);
        Scratch.Git(behind, "remote", "set-url", "origin", code.At("missing.git"));

        var result = (await CodeCommands.PullThenPush([behind])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).DoesNotContain("To ");
    }
}
