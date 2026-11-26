using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

/// <summary>
/// History read in process reads what git does, and leaves to git what it can't read as git would.
/// </summary>
public class LibGitHistoryTests
{
    // The same commits in the same order as git log, made in the same second as they are here,
    // and a page part way down: a merge's two parents, tags light and annotated, a subject of two
    // lines, which git joins, and the files each changed, a rename's and a merge's included.
    [Test]
    public async Task ReadsAsGitDoes()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "tag", "v1.0");
        Git(repo, "switch", "-q", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(repo, "side.txt"), "side");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Side");
        Git(repo, "switch", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "main.txt"), "main");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "Main\nwith a second line", "-m", "The body.");
        Git(repo, "merge", "--no-ff", "-m", "Merge side", "side");
        Git(repo, "tag", "-a", "v2.0", "-m", "Release");
        Git(repo, "mv", "tracked.txt", "renamed.txt");
        Git(repo, "commit", "-m", "Rename");

        var git = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;
        var libGit = LibGitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;

        await Assert.That(Lines(libGit)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);
        await Assert.That(libGit.Select(_ => _.Subject)).IsEquivalentTo(["Rename", "Merge side", "Main with a second line", "Side", "Initial"], CollectionOrdering.Matching);
        await Assert.That(libGit[1].Tags).IsEquivalentTo(["v2.0"]);
        await Assert.That(libGit[^1].Tags).IsEquivalentTo(["v1.0"]);
        await Assert.That(Lines(LibGitHistory.Read(repo, 2, 2, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.Read(repo, 2, 2, Cancel.None)!), CollectionOrdering.Matching);

        foreach (var commit in git)
        {
            await Assert.That(Lines(LibGitHistory.FilesOf(repo, commit, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.FilesOf(repo, commit, Cancel.None)!), CollectionOrdering.Matching);
        }

        await Assert.That(Lines(LibGitHistory.FilesOf(repo, git[0], Cancel.None)!)).IsEquivalentTo(["Renamed renamed.txt tracked.txt"]);
    }

    // The commits the branch it tracks hasn't got; none without one tracked.
    [Test]
    public async Task Unpushed()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await Assert.That(LibGitHistory.Unpushed(repo, Cancel.None)).IsEmpty();

        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Git(repo, "commit", "-am", "Local");
        var head = Git(repo, "rev-parse", "HEAD");
        await Assert.That(LibGitHistory.Unpushed(repo, Cancel.None)).IsEquivalentTo([head]);

        Git(repo, "switch", "-q", "-c", "untracked");
        await Assert.That(LibGitHistory.Unpushed(repo, Cancel.None)).IsEmpty();
    }

    // Before the first commit there is no history; a folder that isn't a repository can't be read.
    [Test]
    public async Task NoCommitsYet()
    {
        using var scratch = new Scratch();
        var repo = scratch.At("Fresh");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--initial-branch=main");
        var plain = scratch.At("Plain");
        Directory.CreateDirectory(plain);

        await Assert.That(LibGitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)).IsEmpty();
        await Assert.That(LibGitHistory.Read(plain, 0, GitHistory.Page, Cancel.None)).IsNull();
        await Assert.That(LibGitHistory.Unpushed(plain, Cancel.None)).IsNull();
    }

    // With a .mailmap, which LibGit2Sharp can't follow, the commits are read by git, which names
    // the author as it says.
    [Test]
    public async Task MailmapLeftToGit()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".mailmap"), "Mapped Name <test@example.com>\n");

        await Assert.That(LibGitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)).IsNull();
        var commits = LibGitHistory.ReadFaster(repo, 0, GitHistory.Page, Cancel.None)!;
        await Assert.That(commits.Select(_ => _.Author)).IsEquivalentTo(["Mapped Name"]);
    }

    static List<string> Lines(List<GitCommit> commits) =>
        commits
            .Select(_ => $"{_.Sha} [{string.Join(' ', _.Parents)}] {_.Author} {_.When.ToUnixTimeSeconds()} [{string.Join(' ', _.Tags)}] {_.Subject}")
            .ToList();

    static List<string> Lines(List<Change> files) =>
        files
            .Select(_ => $"{_.Kind} {_.Path} {_.OldPath}".Trim())
            .ToList();
}
