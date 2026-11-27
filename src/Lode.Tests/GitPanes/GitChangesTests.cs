using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

public class GitChangesTests
{
    // Each file on its own, a renamed one with where it was, a copied one without, a conflicted
    // one, and the branch.
    [Test]
    public async Task Parse()
    {
        var output = string.Join(
            '\0',
            "# branch.oid 0123456789abcdef0123456789abcdef01234567",
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaa bbb Docs/guide.aleph",
            "2 R. N... 100644 100644 100644 aaa bbb R100 report one.beta",
            "report2.beta",
            "2 C. N... 100644 100644 100644 aaa bbb C75 copy.beta",
            "original.beta",
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc both.aleph",
            "? Source/Deep/new.aleph",
            "? Source/Deep/other.aleph",
            "1 D. N... 100644 000000 000000 aaa 000 gone.aleph",
            "");

        var changes = GitChanges.Parse(output, merging: false);

        await Assert.That(changes.Branch).IsEqualTo("main");
        await Assert.That(changes.Head).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(changes.HasCommits).IsTrue();
        await Assert.That(changes.Conflicted).IsTrue();
        await Assert.That(changes.Files.Select(_ => $"{_.Kind} {_.Path} {_.OldPath}".Trim()))
            .IsEquivalentTo(
                [
                    "Added copy.beta",
                    "Modified Docs/guide.aleph",
                    "Conflicted both.aleph",
                    "Deleted gone.aleph",
                    "Renamed report one.beta report2.beta",
                    "Untracked Source/Deep/new.aleph",
                    "Untracked Source/Deep/other.aleph"
                ]);
    }

    [Test]
    public async Task ParseBeforeTheFirstCommit()
    {
        var changes = GitChanges.Parse("# branch.oid (initial)\0# branch.head main\0? a.txt\0", merging: false);

        await Assert.That(changes.HasCommits).IsFalse();
        await Assert.That(changes.Head).IsNull();
        await Assert.That(changes.Files.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ParseDetached()
    {
        var changes = GitChanges.Parse("# branch.oid 0123\0# branch.head (detached)\0", merging: false);

        await Assert.That(changes.Branch).IsNull();
    }

    [Test]
    public async Task Message()
    {
        await Assert.That(GitChanges.Message("  Summary ", "")).IsEqualTo("Summary");
        await Assert.That(GitChanges.Message("Summary", "\nThe why.\n")).IsEqualTo("Summary\n\nThe why.");
    }

    // A commit's message back as a summary and description: its first paragraph on one line, as
    // git's subject is, then the rest.
    [Test]
    public async Task SplitMessage()
    {
        await Assert.That(GitChanges.SplitMessage("Summary\n")).IsEqualTo(("Summary", ""));
        await Assert.That(GitChanges.SplitMessage("Summary\n\nThe why.\n\nMore.\n")).IsEqualTo(("Summary", "The why.\n\nMore."));
        await Assert.That(GitChanges.SplitMessage("A summary\r\nover two lines\r\n\r\nThe why.")).IsEqualTo(("A summary over two lines", "The why."));
    }

    // Amending, the ticked files go in the last commit, in its place, with the message given and
    // its author; what else is changed or staged stays so.
    [Test]
    public async Task AmendsTheLastCommit()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        var parent = Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "first.txt"), "first");
        Git(repo, "add", ".");
        Git(repo, "commit", "--author=Ada <ada@example.com>", "-m", "First");
        await File.WriteAllTextAsync(Path.Combine(repo, "second.txt"), "second");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged");
        Git(repo, "add", "staged.txt");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        var result = GitChanges.Commit(repo, changes.Files.Where(_ => _.Path == "second.txt").ToList(), false, "First and second", Cancel.None, amend: true);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-parse", "HEAD^")).IsEqualTo(parent);
        await Assert.That(Git(repo, "log", "-1", "--format=%an: %s")).IsEqualTo("Ada: First and second");
        await Assert.That(Git(repo, "show", "--name-only", "--format=", "HEAD").Split('\n')).IsEquivalentTo(["first.txt", "second.txt"]);
        await Assert.That(Git(repo, "diff", "--name-only")).IsEqualTo("tracked.txt");
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("staged.txt");
    }

    // Amending with nothing ticked changes only the message; what is staged stays staged, out
    // of it.
    [Test]
    public async Task AmendsOnlyTheMessage()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        var parent = Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "first.txt"), "first");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Frist");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged");
        Git(repo, "add", "staged.txt");

        var result = GitChanges.Commit(repo, [], false, "First\n\nThe why.", Cancel.None, amend: true);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-parse", "HEAD^")).IsEqualTo(parent);
        await Assert.That(Git(repo, "log", "-1", "--format=%B")).IsEqualTo("First\n\nThe why.");
        await Assert.That(Git(repo, "show", "--name-only", "--format=", "HEAD")).IsEqualTo("first.txt");
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("staged.txt");
    }

    // A line matching the file alone, from the top, what a pattern takes as more escaped.
    [Test]
    public async Task IgnoreLine()
    {
        await Assert.That(GitChanges.IgnoreLine("bin/out.txt")).IsEqualTo("/bin/out.txt");
        await Assert.That(GitChanges.IgnoreLine("[x]*?.txt")).IsEqualTo("/\\[x]\\*\\?.txt");
        await Assert.That(GitChanges.IgnoreLine("trailing  ")).IsEqualTo("/trailing \\ ");
        await Assert.That(GitChanges.IgnoreLine("#hash")).IsEqualTo("/#hash");
    }

    // Only the files ticked go in the commit, a rename both where it went and where it was, a
    // staged deletion, a file in a new folder, and names a pattern would read otherwise. What
    // else is changed stays changed, what else is staged staged.
    [Test]
    public async Task CommitsOnlyTheTickedFiles()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "moved.txt"), "moved");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged");
        await File.WriteAllTextAsync(Path.Combine(repo, "deleted.txt"), "deleted");
        await File.WriteAllTextAsync(Path.Combine(repo, "left.txt"), "left");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "More");

        Git(repo, "mv", "moved.txt", "renamed.txt");
        Git(repo, "rm", "-q", "deleted.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "staged, changed");
        Git(repo, "add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "left.txt"), "left, changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Directory.CreateDirectory(Path.Combine(repo, "new"));
        await File.WriteAllTextAsync(Path.Combine(repo, "new", "[x].txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(repo, "-dash.txt"), "dash");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        string[] ticked = ["renamed.txt", "deleted.txt", "tracked.txt", "new/[x].txt", "-dash.txt"];
        var result = GitChanges.Commit(repo, changes.Files.Where(_ => ticked.Contains(_.Path)).ToList(), false, "Some\n\n# kept", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "show", "--name-status", "--format=", "HEAD").Split('\n').Order())
            .IsEquivalentTo(["A\t-dash.txt", "A\tnew/[x].txt", "D\tdeleted.txt", "M\ttracked.txt", "R100\tmoved.txt\trenamed.txt"]);
        await Assert.That(Git(repo, "log", "-1", "--format=%B")).IsEqualTo("Some\n\n# kept");
        await Assert.That(Git(repo, "diff", "--name-only")).IsEqualTo("left.txt");
        await Assert.That(Git(repo, "diff", "--cached", "--name-only")).IsEqualTo("staged.txt");
    }

    // A repository with no commit yet commits what is ticked.
    [Test]
    public async Task CommitsTheFirstCommit()
    {
        using var scratch = new Scratch();
        var repo = scratch.At("Fresh");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--initial-branch=main");
        Git(repo, "config", "user.name", "Test");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(repo, "two.txt"), "two");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        await Assert.That(changes.HasCommits).IsFalse();
        var result = GitChanges.Commit(repo, changes.Files.Where(_ => _.Path == "one.txt").ToList(), false, "First", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "show", "--name-only", "--format=", "HEAD")).IsEqualTo("one.txt");
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("?? two.txt");
    }

    // Part way through a merge git commits everything, so the commit does.
    [Test]
    public async Task CommitsAMergeWhole()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "switch", "-q", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(repo, "side.txt"), "side");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Side");
        Git(repo, "switch", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "main.txt"), "main");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Main");
        Git(repo, "merge", "--no-commit", "--no-ff", "side");
        await File.WriteAllTextAsync(Path.Combine(repo, "extra.txt"), "extra");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        await Assert.That(changes.Merging).IsTrue();
        var result = GitChanges.Commit(repo, [changes.Files[0]], changes.Merging, "Merge side", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length).IsEqualTo(3);
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // A merge left part way by conflicts, as Changes reads it: each file in conflict with how
    // many conflicts are marked in it, the branch merged in, and git's message for the merge;
    // one a side deleted has a side to pick.
    [Test]
    public async Task ReadsConflicts()
    {
        using var scratch = new Scratch();
        var repo = await Conflicting(scratch);

        var changes = GitChanges.Read(repo, Cancel.None)!;

        await Assert.That((changes.Merging, changes.MergingFrom, changes.MergeSummary, changes.ConflictsLeft)).IsEqualTo((true, "feature", "Merge branch 'feature'", 2));
        await Assert.That(changes.Files.Select(_ => $"{_.Path}: {_.Conflict?.Note}"))
            .IsEquivalentTo(["gone.txt: Pick a side", "tracked.txt: 2 conflicts"]);
        await Assert.That(ChangesPane.MergeBarText(changes)).IsEqualTo("Merging feature into main: 2 conflicted files left. Right-click one to pick a side, or settle its markers in the editor.");
    }

    // Settled in its text, no markers left, a file can go in the commit as it is; one side's
    // file picked, or a deletion, settles it with git; the merge then commits.
    [Test]
    public async Task SettlesConflictsAndCommitsTheMerge()
    {
        using var scratch = new Scratch();
        var repo = await Conflicting(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "settled\n");
        var read = GitChanges.Read(repo, Cancel.None)!;
        await Assert.That((read.ConflictsLeft, read.Files.Single(_ => _.Path == "tracked.txt").Settled)).IsEqualTo((1, true));

        var gone = read.Files.Single(_ => _.Path == "gone.txt");
        var deleted = GitChanges.UseSide(repo, gone, ours: false, Cancel.None);
        var settled = GitChanges.Read(repo, Cancel.None)!;

        await Assert.That(deleted.Succeeded).IsTrue();
        await Assert.That(File.Exists(Path.Combine(repo, "gone.txt"))).IsFalse();
        await Assert.That((settled.Conflicted, ChangesPane.MergeBarText(settled))).IsEqualTo((false, "Merging feature into main: no conflicts left. Commit the merge."));

        var commit = GitChanges.Commit(repo, settled.Files, merging: true, "Merge branch 'feature'", Cancel.None);

        await Assert.That(commit.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length).IsEqualTo(3);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("settled\n");
    }

    // The branch checked out's file whole, or the branch merged in's, and Mark as resolved
    // taking a file as it is. Each is checked out with the line endings git gives it.
    [Test]
    public async Task UsesEitherSidesFile()
    {
        using var scratch = new Scratch();
        var repo = await Conflicting(scratch);
        var read = GitChanges.Read(repo, Cancel.None)!;
        var tracked = read.Files.Single(_ => _.Path == "tracked.txt");

        var theirs = GitChanges.UseSide(repo, tracked, ours: false, Cancel.None);
        var afterTheirs = await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"));
        var gone = read.Files.Single(_ => _.Path == "gone.txt");
        var ours = GitChanges.UseSide(repo, gone, ours: true, Cancel.None);
        var afterOurs = await File.ReadAllTextAsync(Path.Combine(repo, "gone.txt"));

        await Assert.That((theirs.Succeeded, ours.Succeeded)).IsEqualTo((true, true));
        await Assert.That(afterTheirs.ReplaceLineEndings("\n")).IsEqualTo("feature one\na\nb\nc\nd\nfeature two\n");
        await Assert.That(afterOurs.ReplaceLineEndings("\n")).IsEqualTo("main changed it\n");
        await Assert.That(GitChanges.Read(repo, Cancel.None)!.Conflicted).IsFalse();
        await Assert.That(Git(repo, "diff", "--name-only", "--diff-filter=U")).IsEqualTo("");
    }

    // Abort merge puts the branch and its files back as they were before it.
    [Test]
    public async Task AbortsTheMerge()
    {
        using var scratch = new Scratch();
        var repo = await Conflicting(scratch);
        var head = Git(repo, "rev-parse", "HEAD");

        var result = GitChanges.Abort(repo, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(GitChanges.Read(repo, Cancel.None)!.Merging).IsFalse();
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
        await Assert.That(Git(repo, "rev-parse", "HEAD")).IsEqualTo(head);
    }

    [Test]
    public async Task CountsMarkersAndNamesTheBranch()
    {
        await Assert.That(GitChanges.CountMarkers("a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> x\n<<<<<<< HEAD\n")).IsEqualTo(2);
        await Assert.That(GitChanges.CountMarkers("no <<<<<<< at the start\n")).IsEqualTo(0);
        await Assert.That(GitChanges.MergedBranch("Merge remote-tracking branch 'origin/main'")).IsEqualTo("origin/main");
        await Assert.That(GitChanges.MergedBranch("Merge branch 'feature' of https://example.com/repo")).IsEqualTo("feature");
        await Assert.That(GitChanges.MergedBranch("Revert \"x\"")).IsNull();
    }

    // Main and feature both changed tracked.txt, twice apart, and feature deleted gone.txt that
    // main changed, then feature merged into main, left part way. Line endings are changed as
    // git for Windows has them unless told otherwise, as CI's does, so git warns of gone.txt's,
    // which is no file in conflict.
    static async Task<string> Conflicting(Scratch scratch)
    {
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", "core.autocrlf", "true");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "one\na\nb\nc\nd\ntwo\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "gone.txt"), "gone\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Base");
        Git(repo, "switch", "-q", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "feature one\na\nb\nc\nd\nfeature two\n");
        Git(repo, "rm", "-q", "gone.txt");
        Git(repo, "commit", "-am", "Feature");
        Git(repo, "switch", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "main one\na\nb\nc\nd\nmain two\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "gone.txt"), "main changed it\n");
        Git(repo, "commit", "-am", "Main");
        var merge = GitMerging.Merge(repo, "feature", squash: false, Cancel.None);
        await Assert.That(merge.Output).StartsWith("Merging feature left 2 conflicted files, to settle in Changes");
        return repo;
    }

    // Puts tracked files back as committed, takes a staged new file out of the index, and
    // leaves a file git doesn't track to the Recycle Bin, which the pane sends it to.
    [Test]
    public async Task Restore()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "added.txt"), "added");
        Git(repo, "add", "added.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "untracked");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        var result = GitChanges.Restore(repo, changes.Files, changes.HasCommits, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(Git(repo, "status", "--porcelain").Split('\n').Order())
            .IsEquivalentTo(["?? untracked.txt"]);
    }

    [Test]
    public async Task Ignore()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/");
        await File.WriteAllTextAsync(Path.Combine(repo, "out.log"), "log");

        GitChanges.Ignore(repo, "out.log");

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, ".gitignore"))).IsEqualTo("bin/\n/out.log\n");
        await Assert.That(GitChanges.Read(repo, Cancel.None)!.Files.Select(_ => _.Path)).IsEquivalentTo([".gitignore"]);
    }
}
