/// <summary>
/// A repository's state as git status reports it, and as LibGit2Sharp reads it, which must agree,
/// run for real against throwaway repositories in the temp folder.
/// </summary>
public class GitStateTests
{
    const string commit = "0123456789abcdef0123456789abcdef01234567";

    // git status -z: each entry ends in a NUL.
    static string Entries(params string[] entries) =>
        string.Concat(entries.Select(_ => _ + '\0'));

    [Test]
    public async Task ParsesAClean()
    {
        var state = GitState.Parse(
            Entries(
                $"# branch.oid {commit}",
                "# branch.head main",
                "# branch.upstream origin/main",
                "# branch.ab +0 -0"));

        await Assert.That(state).IsEqualTo(new GitState("main", commit, 0, 0, []) with { Upstream = true });
    }

    // One entry a path, whatever has happened to it, in order of their paths. A rename's entry
    // is followed by where it was, which is no path of its own.
    [Test]
    public async Task ParsesEachKindOfChange()
    {
        var state = GitState.Parse(
            Entries(
                $"# branch.oid {commit}",
                "# branch.head main",
                $"1 .M N... 100644 100644 100644 {commit} {commit} changed.txt",
                $"1 M. N... 100644 100644 100644 {commit} {commit} staged.txt",
                $"1 MM N... 100644 100644 100644 {commit} {commit} both.txt",
                $"1 A. N... 000000 100644 100644 {commit} {commit} added.txt",
                $"1 .D N... 100644 100644 000000 {commit} {commit} gone.txt",
                $"1 D. N... 100644 000000 000000 {commit} {commit} removed.txt",
                $"2 R. N... 100644 100644 100644 {commit} {commit} R100 src/renamed.txt",
                "original.txt",
                $"u UU N... 100644 100644 100644 100644 {commit} {commit} {commit} conflicted.txt",
                "? untracked.txt",
                "? New/"));

        await Assert.That(state).IsEqualTo(
            new(
                "main",
                commit,
                0,
                0,
                [
                    new("New", GitChange.Untracked),
                    new("added.txt", GitChange.Added),
                    new("both.txt", GitChange.Modified),
                    new("changed.txt", GitChange.Modified),
                    new("conflicted.txt", GitChange.Conflicted),
                    new("gone.txt", GitChange.Deleted),
                    new("removed.txt", GitChange.Deleted),
                    new("src/renamed.txt", GitChange.Renamed),
                    new("staged.txt", GitChange.Staged),
                    new("untracked.txt", GitChange.Untracked)
                ]));
    }

    // A path may have spaces, and with -z, nothing is quoted.
    [Test]
    public async Task ParsesPathsAsTheyAre()
    {
        var state = GitState.Parse(
            Entries(
                $"# branch.oid {commit}",
                "# branch.head main",
                $"1 .M N... 100644 100644 100644 {commit} {commit} Two Words/café.txt",
                "? ünïcode/"));

        await Assert.That(state.Files)
            .IsEquivalentTo<IReadOnlyList<GitFile>, GitFile>(
                [
                    new("Two Words/café.txt", GitChange.Modified),
                    new("ünïcode", GitChange.Untracked)
                ],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ParsesAheadAndBehind()
    {
        var state = GitState.Parse(
            Entries(
                $"# branch.oid {commit}",
                "# branch.head feature",
                "# branch.upstream origin/feature",
                "# branch.ab +12 -3"));

        await Assert.That(state).IsEqualTo(new GitState("feature", commit, 12, 3, []) with { Upstream = true });
    }

    [Test]
    public async Task ParsesDetached()
    {
        var state = GitState.Parse(
            Entries(
                $"# branch.oid {commit}",
                "# branch.head (detached)"));

        await Assert.That(state).IsEqualTo(new(null, commit, 0, 0, []));
    }

    [Test]
    public async Task ParsesNoCommitsYet()
    {
        var state = GitState.Parse(
            Entries(
                "# branch.oid (initial)",
                "# branch.head main",
                "? first.txt"));

        await Assert.That(state).IsEqualTo(new("main", null, 0, 0, [new("first.txt", GitChange.Untracked)]));
    }

    // CodeCommands.Git adds a line break and what git warned of after the last entry.
    [Test]
    public async Task ParsesWithAWarningAfter()
    {
        var state = GitState.Parse(Entries($"# branch.oid {commit}", "# branch.head main", "? new.txt") + "\nwarning: something");

        await Assert.That(state).IsEqualTo(new("main", commit, 0, 0, [new("new.txt", GitChange.Untracked)]));
    }

    [Test]
    public async Task Describes()
    {
        await Assert.That(State("main", 0, 0, 0).Describe()).IsEqualTo("main");
        await Assert.That(State("main", 1, 0, 0).Describe()).IsEqualTo("main · 1 change");
        await Assert.That(State("main", 1234, 2, 1).Describe()).IsEqualTo("main · 1,234 changes · 2 ahead · 1 behind");
        await Assert.That(State("main", 0, 0, 5).Describe()).IsEqualTo("main · 5 behind");
        await Assert.That(State(null, 2, 0, 0).Describe()).IsEqualTo("Detached at 0123456 · 2 changes");
        await Assert.That(new GitState("main", null, 0, 0, []).Describe()).IsEqualTo("main · no commits yet");
    }

    static GitState State(string? branch, int changes, int ahead, int behind) =>
        new(branch, commit, ahead, behind, Enumerable.Range(0, changes).Select(_ => new GitFile($"{_}.txt", GitChange.Modified)).ToList());

    // The same files make the same state, whichever lists hold them.
    [Test]
    public async Task EqualByTheirFiles()
    {
        var one = new GitState("main", commit, 0, 0, [new("a.txt", GitChange.Modified)]);
        var same = new GitState("main", commit, 0, 0, [new("a.txt", GitChange.Modified)]);
        var other = new GitState("main", commit, 0, 0, [new("a.txt", GitChange.Staged)]);

        await Assert.That(one).IsEqualTo(same);
        await Assert.That(one).IsNotEqualTo(other);
    }

    [Test]
    public async Task ReadsAClean()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");

        var state = await Read(repo);

        await Assert.That(state).IsEqualTo(new GitState("main", Head(repo), 0, 0, []) with { Upstream = true });
    }

    // A file changed, one staged, one untracked and a folder of them, each one change, and one
    // in a folder by its path from the top. What git ignores isn't one.
    [Test]
    public async Task ReadsChanges()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "deep.txt"), "deep");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "More");

        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "deep.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged");
        CodeCommandsTests.Scratch.Git(repo, "add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "loose.txt"), "loose");
        Directory.CreateDirectory(Path.Combine(repo, "New"));
        await File.WriteAllTextAsync(Path.Combine(repo, "New", "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(repo, "New", "two.txt"), "two");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        await File.WriteAllTextAsync(Path.Combine(repo, "bin", "out.dll"), "built");

        var state = await Read(repo);

        await Assert.That(state).IsEqualTo(
            new GitState(
                "main",
                Head(repo),
                1,
                0,
                [
                    new("New", GitChange.Untracked),
                    new("loose.txt", GitChange.Untracked),
                    new("src/deep.txt", GitChange.Modified),
                    new("staged.txt", GitChange.Added),
                    new("tracked.txt", GitChange.Modified)
                ]) with
            {
                Upstream = true
            });
    }

    // Staged as a rename, the file is one change, not one gone and one new. One deleted, staged
    // or not, is one too.
    [Test]
    public async Task ReadsARenameAndDeletions()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Commit(repo, "second.txt");
        Commit(repo, "third.txt");
        CodeCommandsTests.Scratch.Git(repo, "mv", "tracked.txt", "renamed.txt");
        CodeCommandsTests.Scratch.Git(repo, "rm", "second.txt");
        File.Delete(Path.Combine(repo, "third.txt"));

        var state = await Read(repo);

        await Assert.That(state.Files)
            .IsEquivalentTo<IReadOnlyList<GitFile>, GitFile>(
                [
                    new("renamed.txt", GitChange.Renamed),
                    new("second.txt", GitChange.Deleted),
                    new("third.txt", GitChange.Deleted)
                ],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Ahead by its own commit, behind by one another clone pushed, as of its last fetch.
    [Test]
    public async Task ReadsAheadAndBehind()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var other = code.Clone("Repo", "Other");
        Commit(other, "other.txt");
        CodeCommandsTests.Scratch.Git(other, "push");
        Commit(repo, "mine.txt");

        await Assert.That(await Read(repo)).IsEqualTo(new GitState("main", Head(repo), 1, 0, []) with { Upstream = true });

        CodeCommandsTests.Scratch.Git(repo, "fetch");

        await Assert.That(await Read(repo)).IsEqualTo(new GitState("main", Head(repo), 1, 1, []) with { Upstream = true });
    }

    // Both changed the same line: the file is unmerged, mid-merge.
    [Test]
    public async Task ReadsAConflict()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var other = code.Clone("Repo", "Other");
        await File.WriteAllTextAsync(Path.Combine(other, "tracked.txt"), "theirs");
        CodeCommandsTests.Scratch.Git(other, "commit", "-am", "Theirs");
        CodeCommandsTests.Scratch.Git(other, "push");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "mine");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-am", "Mine");
        CodeCommandsTests.Scratch.Git(repo, "fetch");
        await Assert.That(() => CodeCommandsTests.Scratch.Git(repo, "merge", "origin/main")).Throws<Exception>();

        var state = await Read(repo);

        await Assert.That(state).IsEqualTo(new GitState("main", Head(repo), 1, 1, [new("tracked.txt", GitChange.Conflicted)]) with { Upstream = true });
    }

    [Test]
    public async Task ReadsDetached()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "checkout", "--detach");

        var state = await Read(repo);

        await Assert.That(state).IsEqualTo(new(null, Head(repo), 0, 0, []));
    }

    [Test]
    public async Task ReadsNoCommitsYet()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.At("New");
        Directory.CreateDirectory(repo);
        CodeCommandsTests.Scratch.Git(repo, "init", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(repo, "first.txt"), "first");

        var state = await Read(repo);

        await Assert.That(state).IsEqualTo(new("main", null, 0, 0, [new("first.txt", GitChange.Untracked)]));
    }

    // A worktree has a .git file pointing at the repository's, and a branch of its own.
    [Test]
    public async Task ReadsAWorktree()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var tree = code.At("Tree");
        CodeCommandsTests.Scratch.Git(repo, "worktree", "add", "-b", "feature", tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "tracked.txt"), "changed");

        var state = await Read(tree);

        await Assert.That(state).IsEqualTo(new("feature", Head(tree), 0, 0, [new("tracked.txt", GitChange.Modified)]));
    }

    // Names with spaces and letters beyond ASCII come through as they are, from both.
    [Test]
    public async Task ReadsNamesAsTheyAre()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Directory.CreateDirectory(Path.Combine(repo, "Two Words"));
        await File.WriteAllTextAsync(Path.Combine(repo, "Two Words", "café.txt"), "new");

        var state = await Read(repo);

        await Assert.That(state.Files).IsEquivalentTo([new GitFile("Two Words", GitChange.Untracked)]);
    }

    // As many as git lists, read from the index's header, for a worktree from its own.
    [Test]
    public async Task CountsTheIndexsFiles()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Commit(repo, "second.txt");
        Commit(repo, "third.txt");
        var tree = code.At("Tree");
        CodeCommandsTests.Scratch.Git(repo, "worktree", "add", "-b", "feature", tree);
        CodeCommandsTests.Scratch.Git(tree, "rm", "third.txt");
        var empty = code.At("Empty");
        Directory.CreateDirectory(empty);
        CodeCommandsTests.Scratch.Git(empty, "init");

        await Assert.That(LibGitState.IndexEntries(repo)).IsEqualTo(3);
        await Assert.That(LibGitState.IndexEntries(tree)).IsEqualTo(2);
        await Assert.That(LibGitState.IndexEntries(empty)).IsEqualTo(0);
    }

    // Whichever reads it, the state is the same.
    [Test]
    public async Task TheFasterReadsTheSame()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");

        await Assert.That(LibGitState.FromFaster(repo, Cancel.None)).IsEqualTo(GitState.FromGit(repo, Cancel.None));
    }

    [Test]
    public async Task NotARepositoryIsNull()
    {
        using var code = new CodeCommandsTests.Scratch();
        var folder = code.At("Plain");
        Directory.CreateDirectory(folder);

        await Assert.That(GitState.FromGit(folder, Cancel.None)).IsNull();
        await Assert.That(LibGitState.FromLibGit2(folder, Cancel.None)).IsNull();
    }

    // By both readers, which must agree.
    static async Task<GitState> Read(string repo)
    {
        var git = GitState.FromGit(repo, Cancel.None);
        var libGit = LibGitState.FromLibGit2(repo, Cancel.None);
        await Assert.That(git).IsNotNull();
        await Assert.That(libGit).IsEqualTo(git);
        return git!;
    }

    static string Head(string repo) =>
        CodeCommandsTests.Scratch.Git(repo, "rev-parse", "HEAD");

    static void Commit(string repo, string file)
    {
        File.WriteAllText(Path.Combine(repo, file), file);
        CodeCommandsTests.Scratch.Git(repo, "add", file);
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", file);
    }
}
