using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

public class GitHistoryTests
{
    // Each commit's fields, its tags from what points at it, a merge's two parents, the first
    // commit's none.
    [Test]
    public async Task Parse()
    {
        var output = string.Join(
            '\0',
            "aaa\u001fbbb ccc\u001fAda Lovelace\u001fada@example.com\u001f1743508800\u001fHEAD -> main, tag: v2.0, tag: stable, origin/main\u001fMerge branch 'side'",
            "\nbbb\u001fddd\u001fGrace Hopper\u001fgrace@example.com\u001f1743422400\u001f\u001fFix it",
            "\nddd\u001f\u001fAlan Turing\u001falan@example.com\u001f1743336000\u001ftag: v1.0\u001fInitial");

        var commits = GitHistory.Parse(output);

        await Assert.That(commits.Count).IsEqualTo(3);
        await Assert.That(commits[0].Parents).IsEquivalentTo(["bbb", "ccc"]);
        await Assert.That(commits[0].Tags).IsEquivalentTo(["v2.0", "stable"]);
        await Assert.That(commits[0].Subject).IsEqualTo("Merge branch 'side'");
        await Assert.That(commits[0].When).IsEqualTo(new DateTimeOffset(2025, 4, 1, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(commits[1].Tags).IsEmpty();
        await Assert.That(commits[1].Author).IsEqualTo("Grace Hopper");
        await Assert.That(commits[2].Parents).IsEmpty();
        await Assert.That(commits[2].Tags).IsEquivalentTo(["v1.0"]);
    }

    [Test]
    public async Task ParseFiles()
    {
        var output = string.Join('\0', "M", "a.txt", "R087", "old name.txt", "new name.txt", "C100", "src.txt", "copy.txt", "A", "added.txt", "D", "deleted.txt", "T", "link", "");

        var files = GitHistory.ParseFiles(output);

        await Assert.That(files.Select(_ => $"{_.Kind} {_.Path} {_.OldPath}".Trim()))
            .IsEquivalentTo(
                ["Modified a.txt", "Added added.txt", "Added copy.txt src.txt", "Deleted deleted.txt", "Modified link", "Renamed new name.txt old name.txt"],
                TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    // Read from git: newest first, a page at a time, tags, and what a merge brought in against
    // its first parent. The first commit lists everything in it.
    [Test]
    public async Task ReadsTheHistory()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "tag", "v1.0");
        Git(repo, "switch", "-q", "-c", "side");
        File.WriteAllText(Path.Combine(repo, "side.txt"), "side");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Side");
        Git(repo, "switch", "-q", "main");
        File.WriteAllText(Path.Combine(repo, "main.txt"), "main");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Main");
        Git(repo, "merge", "--no-ff", "-m", "Merge side", "side");

        var commits = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;

        await Assert.That(commits.Select(_ => _.Subject)).IsEquivalentTo(["Merge side", "Main", "Side", "Initial"], TUnit.Assertions.Enums.CollectionOrdering.Any);
        await Assert.That(commits[0].Subject).IsEqualTo("Merge side");
        await Assert.That(commits.Single(_ => _.Subject == "Initial").Tags).IsEquivalentTo(["v1.0"]);
        await Assert.That(GitHistory.Read(repo, 1, 2, Cancel.None)!.Count).IsEqualTo(2);

        var merged = GitHistory.FilesOf(repo, commits[0], Cancel.None)!;
        await Assert.That(merged.Select(_ => _.Path)).IsEquivalentTo(["side.txt"]);
        var first = GitHistory.FilesOf(repo, commits.Single(_ => _.Subject == "Initial"), Cancel.None)!;
        await Assert.That(first.Select(_ => $"{_.Kind} {_.Path}")).IsEquivalentTo(["Added tracked.txt"]);
        await Assert.That(GitHistory.Message(repo, commits[0].Sha)).IsEqualTo("Merge side");
    }

    // The commits the branch it tracks hasn't got; none without one tracked.
    [Test]
    public async Task Unpushed()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await Assert.That(GitHistory.Unpushed(repo, Cancel.None)).IsEmpty();

        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "changed");
        Git(repo, "commit", "-am", "Local");
        var head = Git(repo, "rev-parse", "HEAD");
        await Assert.That(GitHistory.Unpushed(repo, Cancel.None)).IsEquivalentTo([head]);

        Git(repo, "switch", "-q", "-c", "untracked");
        await Assert.That(GitHistory.Unpushed(repo, Cancel.None)).IsEmpty();
    }

    // Before the first commit there is no history, rather than a failure.
    [Test]
    public async Task NoCommitsYet()
    {
        using var scratch = new Scratch();
        var repo = scratch.At("Fresh");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--initial-branch=main");

        await Assert.That(GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)).IsEmpty();
    }
}
