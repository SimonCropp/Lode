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
                ],
                TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    [Test]
    public async Task ParseBeforeTheFirstCommit()
    {
        var changes = GitChanges.Parse("# branch.oid (initial)\0# branch.head main\0? a.txt\0", merging: false);

        await Assert.That(changes.HasCommits).IsFalse();
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
        File.WriteAllText(Path.Combine(repo, "moved.txt"), "moved");
        File.WriteAllText(Path.Combine(repo, "staged.txt"), "staged");
        File.WriteAllText(Path.Combine(repo, "deleted.txt"), "deleted");
        File.WriteAllText(Path.Combine(repo, "left.txt"), "left");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "More");

        Git(repo, "mv", "moved.txt", "renamed.txt");
        Git(repo, "rm", "-q", "deleted.txt");
        File.WriteAllText(Path.Combine(repo, "staged.txt"), "staged, changed");
        Git(repo, "add", "staged.txt");
        File.WriteAllText(Path.Combine(repo, "left.txt"), "left, changed");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "changed");
        Directory.CreateDirectory(Path.Combine(repo, "new"));
        File.WriteAllText(Path.Combine(repo, "new", "[x].txt"), "new");
        File.WriteAllText(Path.Combine(repo, "-dash.txt"), "dash");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        string[] ticked = ["renamed.txt", "deleted.txt", "tracked.txt", "new/[x].txt", "-dash.txt"];
        var result = GitChanges.Commit(repo, changes.Files.Where(_ => ticked.Contains(_.Path)).ToList(), false, "Some\n\n# kept", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "show", "--name-status", "--format=", "HEAD").Split('\n').Order())
            .IsEquivalentTo(["A\t-dash.txt", "A\tnew/[x].txt", "D\tdeleted.txt", "M\ttracked.txt", "R100\tmoved.txt\trenamed.txt"], TUnit.Assertions.Enums.CollectionOrdering.Any);
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
        File.WriteAllText(Path.Combine(repo, "one.txt"), "one");
        File.WriteAllText(Path.Combine(repo, "two.txt"), "two");

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
        File.WriteAllText(Path.Combine(repo, "side.txt"), "side");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Side");
        Git(repo, "switch", "-q", "main");
        File.WriteAllText(Path.Combine(repo, "main.txt"), "main");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Main");
        Git(repo, "merge", "--no-commit", "--no-ff", "side");
        File.WriteAllText(Path.Combine(repo, "extra.txt"), "extra");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        await Assert.That(changes.Merging).IsTrue();
        var result = GitChanges.Commit(repo, [changes.Files[0]], changes.Merging, "Merge side", Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length).IsEqualTo(3);
        await Assert.That(Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // Puts tracked files back as committed, takes a staged new file out of the index, and
    // leaves a file git doesn't track to the Recycle Bin, which the pane sends it to.
    [Test]
    public async Task Restore()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "changed");
        File.WriteAllText(Path.Combine(repo, "added.txt"), "added");
        Git(repo, "add", "added.txt");
        File.WriteAllText(Path.Combine(repo, "untracked.txt"), "untracked");

        var changes = GitChanges.Read(repo, Cancel.None)!;
        var result = GitChanges.Restore(repo, changes.Files, changes.HasCommits, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(Git(repo, "status", "--porcelain").Split('\n').Order())
            .IsEquivalentTo(["?? untracked.txt"], TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    [Test]
    public async Task Ignore()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/");
        File.WriteAllText(Path.Combine(repo, "out.log"), "log");

        GitChanges.Ignore(repo, "out.log");

        await Assert.That(File.ReadAllText(Path.Combine(repo, ".gitignore"))).IsEqualTo("bin/\n/out.log\n");
        await Assert.That(GitChanges.Read(repo, Cancel.None)!.Files.Select(_ => _.Path)).IsEquivalentTo([".gitignore"]);
    }
}
