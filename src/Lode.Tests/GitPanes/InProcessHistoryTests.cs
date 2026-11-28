using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

/// <summary>
/// History read in process reads what git does, and leaves to git what it can't read as git would.
/// </summary>
public class InProcessHistoryTests
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
        var inProcess = InProcessHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;

        await Assert.That(Lines(inProcess)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);
        await Assert.That(inProcess.Select(_ => _.Subject)).IsEquivalentTo(["Rename", "Merge side", "Main with a second line", "Side", "Initial"], CollectionOrdering.Matching);
        await Assert.That(inProcess[1].Tags).IsEquivalentTo(["v2.0"]);
        await Assert.That(inProcess[^1].Tags).IsEquivalentTo(["v1.0"]);
        await Assert.That(Lines(InProcessHistory.Read(repo, 2, 2, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.Read(repo, 2, 2, Cancel.None)!), CollectionOrdering.Matching);

        foreach (var commit in git)
        {
            await Assert.That(Lines(InProcessHistory.FilesOf(repo, commit, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.FilesOf(repo, commit, Cancel.None)!), CollectionOrdering.Matching);
        }

        await Assert.That(Lines(InProcessHistory.FilesOf(repo, git[0], Cancel.None)!)).IsEquivalentTo(["Renamed renamed.txt tracked.txt"]);
    }

    // The commits the branch it tracks hasn't got; none without one tracked.
    [Test]
    public async Task Unpushed()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        await Assert.That(InProcessHistory.Unpushed(repo, Cancel.None)).IsEmpty();

        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Git(repo, "commit", "-am", "Local");
        var head = Git(repo, "rev-parse", "HEAD");
        await Assert.That(InProcessHistory.Unpushed(repo, Cancel.None)).IsEquivalentTo([head]);

        Git(repo, "switch", "-q", "-c", "untracked");
        await Assert.That(InProcessHistory.Unpushed(repo, Cancel.None)).IsEmpty();
    }

    // Never pushed, as git finds it too: on a branch tracking none, the commit checked out on no
    // remote's branch; with no remote, on none. Not so on a branch tracked, one at a remote's
    // branch, detached, or before the first commit.
    [Test]
    public async Task NeverPushed()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        var fresh = scratch.At("Fresh");
        Directory.CreateDirectory(fresh);
        Git(fresh, "init", "--initial-branch=main");
        Identify(fresh);

        static async Task<bool> Both(string folder)
        {
            var inProcess = InProcessHistory.NeverPushed(folder, Cancel.None);
            await Assert.That(inProcess).IsEqualTo(GitHistory.NeverPushed(folder, Cancel.None));
            return inProcess!.Value;
        }

        await Assert.That(await Both(fresh)).IsFalse();
        Git(fresh, "commit", "--allow-empty", "-m", "Only");
        await Assert.That(await Both(fresh)).IsTrue();

        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Git(repo, "commit", "-am", "Local");
        await Assert.That(await Both(repo)).IsFalse();

        Git(repo, "switch", "-q", "--no-track", "-c", "atOrigin", "origin/main");
        await Assert.That(await Both(repo)).IsFalse();

        Git(repo, "commit", "--allow-empty", "-m", "New");
        await Assert.That(await Both(repo)).IsTrue();

        Git(repo, "switch", "-q", "--detach");
        await Assert.That(await Both(repo)).IsFalse();
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

        await Assert.That(InProcessHistory.Read(repo, 0, GitHistory.Page, Cancel.None)).IsEmpty();
        await Assert.That(InProcessHistory.Read(plain, 0, GitHistory.Page, Cancel.None)).IsNull();
        await Assert.That(InProcessHistory.Unpushed(plain, Cancel.None)).IsNull();
        await Assert.That(InProcessHistory.NeverPushed(plain, Cancel.None)).IsNull();
    }

    // With a .mailmap, authors are named as it says, as git log's %aN names them: by email, or by
    // name and email together, whatever their case, a later line winning.
    [Test]
    public async Task FollowsAMailmap()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "-c", "user.name=Other", "-c", "user.email=other@example.com", "commit", "--allow-empty", "-m", "Other");
        Git(repo, "-c", "user.name=Third", "-c", "user.email=Third@Example.com", "commit", "--allow-empty", "-m", "Third");
        await File.WriteAllTextAsync(
            Path.Combine(repo, ".mailmap"),
            """
            # Who is who
            Mapped Name <test@example.com>
            Wrong Name <other@example.com>
            Right Name <right@example.com> other <OTHER@example.com>
            Later Name <test@example.com>
            Third Person <third@example.com>
            """);

        var git = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;
        var inProcess = InProcessHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;

        await Assert.That(Lines(inProcess)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);
        await Assert.That(inProcess.Select(_ => _.Author)).IsEquivalentTo(["Third Person", "Right Name", "Later Name"], CollectionOrdering.Matching);
    }

    // A .mailmap's lines git can't take passed over: no email, one not closed, an empty one first;
    // mailmap.file read after it, winning; mailmap.blob, read from an object, left to git.
    [Test]
    public async Task FollowsMailmapsAsGitDoes()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "-c", "user.name=Other", "-c", "user.email=other@example.com", "commit", "--allow-empty", "-m", "Other");
        Git(repo, "-c", "user.name=Unmapped", "-c", "user.email=unmapped@example.com", "commit", "--allow-empty", "-m", "Unmapped");
        await File.WriteAllTextAsync(
            Path.Combine(repo, ".mailmap"),
            "No Email\nUnclosed <test@example.com\n<> <test@example.com>\nNamed Only <other@example.com>\n<emailonly@example.com> <test@example.com>\n");
        var file = scratch.At("mailmap");
        await File.WriteAllTextAsync(file, "From The File <other@example.com>\n");
        Git(repo, "config", "mailmap.file", file.Replace('\\', '/'));

        await Assert.That(Lines(InProcessHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!), CollectionOrdering.Matching);

        Git(repo, "config", "mailmap.blob", "HEAD:.mailmap");
        await Assert.That(InProcessHistory.Read(repo, 0, GitHistory.Page, Cancel.None)).IsNull();
    }

    // Packed as git gc leaves a repository, walked by its commit graph, as git log walks it: a
    // page part way down too, several tags on one commit in the order git lists them, and the
    // files a commit changed, a rename with lines changed among them.
    [Test]
    public async Task ReadsAPackedHistoryAsGitDoes()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        var lines = Enumerable.Range(0, 60).Select(_ => $"Line number {_}").ToList();
        for (var index = 0; index < 30; index++)
        {
            lines[index] = $"Changed by {index}";
            await File.WriteAllTextAsync(Path.Combine(repo, $"file{index % 4}.txt"), string.Join('\n', lines));
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", $"Commit {index}");
        }

        Git(repo, "tag", "beta");
        Git(repo, "tag", "alpha");
        Git(repo, "tag", "-a", "gamma", "-m", "Gamma");
        Directory.CreateDirectory(Path.Combine(repo, "moved"));
        Git(repo, "mv", "file1.txt", "moved/file1-renamed.txt");
        await File.AppendAllTextAsync(Path.Combine(repo, "moved", "file1-renamed.txt"), "\nOne more line");
        Git(repo, "rm", "-q", "file2.txt");
        Git(repo, "commit", "-q", "-am", "Move and remove");
        Git(repo, "gc", "--quiet");

        var git = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;
        var inProcess = InProcessHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;

        await Assert.That(Lines(inProcess)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);
        await Assert.That(inProcess[1].Tags).IsEquivalentTo(["gamma", "beta", "alpha"], CollectionOrdering.Matching);
        await Assert.That(Lines(InProcessHistory.Read(repo, 7, 5, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.Read(repo, 7, 5, Cancel.None)!), CollectionOrdering.Matching);
        foreach (var commit in git.Take(5))
        {
            await Assert.That(Lines(InProcessHistory.FilesOf(repo, commit, Cancel.None)!)).IsEquivalentTo(Lines(GitHistory.FilesOf(repo, commit, Cancel.None)!), CollectionOrdering.Matching);
        }

        await Assert.That(Lines(InProcessHistory.FilesOf(repo, git[0], Cancel.None)!)).IsEquivalentTo(["Deleted file2.txt", "Renamed moved/file1-renamed.txt file1.txt"], CollectionOrdering.Matching);
    }

    // Read a page at a time, as History reads them, each page goes on from where the one before
    // stopped: it reads only its own commits, and the parent of its last, never those before it
    // again, as each did walking from HEAD, so the page 5,000 commits down walked 5,000. And the
    // commits are those of one read of them all, in its order, as git lists them: a merge's two
    // sides, of commits made in the same second, taken in the order git takes them, from their
    // objects and from the commit graph git gc writes.
    [Test]
    public async Task PagesGoOnFromWhereTheLastStopped()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "switch", "-q", "-c", "side");
        for (var index = 0; index < 5; index++)
        {
            Git(repo, "commit", "-q", "--allow-empty", "-m", $"Side {index}");
        }

        Git(repo, "switch", "-q", "main");
        for (var index = 0; index < 5; index++)
        {
            Git(repo, "commit", "-q", "--allow-empty", "-m", $"Main {index}");
        }

        Git(repo, "merge", "-q", "--no-ff", "-m", "Merge side", "side");
        for (var index = 0; index < 12; index++)
        {
            Git(repo, "commit", "-q", "--allow-empty", "-m", $"Later {index}");
        }

        static (List<GitCommit> Commits, List<int> Read) Paged(string repo)
        {
            var walk = new HistoryWalk();
            var commits = new List<GitCommit>();
            var read = new List<int>();
            while (InProcessHistory.Read(repo, commits.Count, 4, walk, Cancel.None) is { Count: > 0 } page)
            {
                commits.AddRange(page);
                read.Add(walk.CommitsRead);
            }

            return (commits, read);
        }

        var git = GitHistory.Read(repo, 0, GitHistory.Page, Cancel.None)!;
        var loose = Paged(repo);
        Git(repo, "gc", "--quiet");
        var graphed = Paged(repo);

        await Assert.That(git.Count).IsEqualTo(24);
        await Assert.That(Lines(loose.Commits)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);
        await Assert.That(Lines(graphed.Commits)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);

        // The first three pages are of the twelve commits after the merge, one after another:
        // each reads its four, and the parent of its last, which the next starts from.
        await Assert.That(loose.Read.Take(3)).IsEquivalentTo([5, 5, 5], CollectionOrdering.Matching);
        await Assert.That(graphed.Read.Take(3)).IsEquivalentTo([5, 5, 5], CollectionOrdering.Matching);
    }

    // A shallow clone's oldest commit has no parents as far as git log says, and the commits
    // are read from their objects, as a commit graph would have them going on.
    [Test]
    public async Task ReadsAShallowCloneAsGitDoes()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), $"{index}");
            Git(repo, "commit", "-q", "-am", $"Commit {index}");
        }

        Git(repo, "push", "-q");
        var shallow = scratch.At("Shallow");
        Git(scratch.Root, "clone", "-q", "--depth", "2", new Uri(scratch.Origin("Repo")).AbsoluteUri, shallow);

        var git = GitHistory.Read(shallow, 0, GitHistory.Page, Cancel.None)!;
        var inProcess = InProcessHistory.Read(shallow, 0, GitHistory.Page, Cancel.None)!;

        await Assert.That(git.Count).IsEqualTo(2);
        await Assert.That(Lines(inProcess)).IsEquivalentTo(Lines(git), CollectionOrdering.Matching);
        await Assert.That(inProcess[^1].Parents).IsEmpty();
        await Assert.That(InProcessHistory.Unpushed(shallow, Cancel.None)).IsEmpty();
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
