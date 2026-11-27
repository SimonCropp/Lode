/// <summary>
/// A repository's state as git status reports it, and as it is read in process from the files
/// git keeps, which must agree, run for real against throwaway repositories in the temp folder.
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
                CollectionOrdering.Matching);
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
        await Assert.That((State("main", 0, 1, 0) with { ForkBehind = 12 }).Describe()).IsEqualTo("main · 1 ahead · 12 behind upstream");
        await Assert.That(State(null, 2, 0, 0).Describe()).IsEqualTo("Detached at 0123456 · 2 changes");
        await Assert.That(new GitState("main", null, 0, 0, []).Describe()).IsEqualTo("main · no commits yet");
    }

    // The status bar shows each part on its own, to act on: the same text as Describe, part by
    // part.
    [Test]
    public async Task Parts()
    {
        var state = State("main", 3, 2, 1) with
        {
            ForkBehind = 4
        };

        await Assert.That(state.Parts())
            .IsEquivalentTo<IList<GitState.Part>, GitState.Part>(
                [
                    new(GitState.PartKind.Branch, "main"),
                    new(GitState.PartKind.Changes, "3 changes"),
                    new(GitState.PartKind.Ahead, "2 ahead"),
                    new(GitState.PartKind.Behind, "1 behind"),
                    new(GitState.PartKind.ForkBehind, "4 behind upstream")
                ],
                CollectionOrdering.Matching);
        await Assert.That(new GitState("main", null, 0, 0, []).Parts().Select(_ => _.Kind))
            .IsEquivalentTo([GitState.PartKind.Branch, GitState.PartKind.NoCommits], CollectionOrdering.Matching);
        await Assert.That(string.Join(" · ", state.Parts().Select(_ => _.Text))).IsEqualTo(state.Describe());
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
                CollectionOrdering.Matching);
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

    // In a fork, how many commits upstream's HEAD has that the one checked out hasn't, as of the
    // last fetch of upstream.
    [Test]
    public async Task ReadsHowFarBehindUpstreamAForkIs()
    {
        using var code = new CodeCommandsTests.Scratch();
        var fork = code.CreateFork("Repo");
        code.CommitUpstream("Repo", "one.txt", "one");
        code.CommitUpstream("Repo", "two.txt", "two");

        await Assert.That(await Read(fork)).IsEqualTo(new GitState("main", Head(fork), 0, 0, []) with { Upstream = true });

        CodeCommandsTests.Scratch.Git(fork, "fetch", "upstream");

        await Assert.That(await Read(fork)).IsEqualTo(new GitState("main", Head(fork), 0, 0, []) with { Upstream = true, ForkBehind = 2 });
    }

    // An upstream added by hand, its HEAD not recorded, gives nothing to count against.
    [Test]
    public async Task AForkWithoutUpstreamsHeadIsBehindNothing()
    {
        using var code = new CodeCommandsTests.Scratch();
        var fork = code.CreateFork("Repo");
        code.CommitUpstream("Repo", "one.txt", "one");
        CodeCommandsTests.Scratch.Git(fork, "fetch", "upstream");
        CodeCommandsTests.Scratch.Git(fork, "remote", "set-head", "upstream", "--delete");

        await Assert.That((await Read(fork)).ForkBehind).IsEqualTo(0);
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

    // A worktree's staged changes are in its own index, in the folder its .git file names, and
    // the repository's are in its: neither has the other's. One with nothing staged yet has no
    // index at all.
    [Test]
    public async Task ReadsEachWorktreesOwnIndex()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Commit(repo, "second.txt");
        Commit(repo, "third.txt");
        var tree = code.At("Tree");
        CodeCommandsTests.Scratch.Git(repo, "worktree", "add", "-b", "feature", tree);
        CodeCommandsTests.Scratch.Git(tree, "rm", "-q", "third.txt");
        var empty = code.At("Empty");
        Directory.CreateDirectory(empty);
        CodeCommandsTests.Scratch.Git(empty, "init", "-q");

        var main = await Read(repo);
        var worktree = await Read(tree);
        var none = await Read(empty);

        await Assert.That(main.Files).IsEmpty();
        await Assert.That(worktree.Files).IsEquivalentTo([new GitFile("third.txt", GitChange.Deleted)]);
        await Assert.That(none.Files).IsEmpty();
    }

    // The most recent tag the commit has in its history, annotated or not: its own, else the
    // nearest back. One the commit's history doesn't reach, on a branch apart, isn't it.
    [Test]
    public async Task ReadsTheMostRecentTag()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var noTags = (await Read(repo)).Tag;

        CodeCommandsTests.Scratch.Git(repo, "tag", "v1.0");
        var own = (await Read(repo)).Tag;

        Commit(repo, "second.txt");
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "v2.0", "-m", "Two");
        Commit(repo, "third.txt");
        var back = (await Read(repo)).Tag;

        CodeCommandsTests.Scratch.Git(repo, "switch", "-c", "feature", "v1.0");
        Commit(repo, "feature.txt");
        var onABranch = (await Read(repo)).Tag;

        CodeCommandsTests.Scratch.Git(repo, "switch", "--orphan", "orphan");
        Commit(repo, "orphan.txt");
        var noneReached = (await Read(repo)).Tag;

        await Assert.That(noTags).IsNull();
        await Assert.That(own).IsEqualTo("v1.0");
        await Assert.That(back).IsEqualTo("v2.0");
        await Assert.That(onABranch).IsEqualTo("v1.0");
        await Assert.That(noneReached).IsNull();
    }

    // Whichever reads it, the state is the same, its tag included.
    [Test]
    public async Task TheFasterReadsTheSame()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "tag", "v1.0");
        Commit(repo, "second.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        var git = GitState.FromGit(repo, Cancel.None);

        await Assert.That(git?.Tag).IsEqualTo("v1.0");
        await Assert.That(InProcessState.FromFaster(repo, Cancel.None)).IsEqualTo(git);
    }

    [Test]
    public async Task NotARepositoryIsNull()
    {
        using var code = new CodeCommandsTests.Scratch();
        var folder = code.At("Plain");
        Directory.CreateDirectory(folder);

        await Assert.That(GitState.FromGit(folder, Cancel.None)).IsNull();
        await Assert.That(InProcessState.FromFiles(folder, Cancel.None)).IsNull();
    }

    // A file whose times aren't those staged, touched say, is hashed as git would stage it to see
    // whether it changed: CRLFs in it made LFs where core.autocrlf or text says, but not where
    // what is staged has CRLFs already, nor in a binary file. One changed to the same size is a
    // change.
    [Test]
    public async Task HashesAFileWhoseTimesChanged()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.autocrlf", "true");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "*.bin binary\n*.text text\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "lines.txt"), "one\r\ntwo\r\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "lines.text"), "one\r\ntwo\r\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "data.bin"), "a\r\nb\0\r\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "same.txt"), "abc\n");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Lines");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "one\r\ntwo\r\n");
        CodeCommandsTests.Scratch.Git(repo, "-c", "core.autocrlf=false", "add", "staged.txt");
        CodeCommandsTests.Scratch.Git(repo, "-c", "core.autocrlf=false", "commit", "-m", "CRLF staged");
        await File.WriteAllTextAsync(Path.Combine(repo, "same.txt"), "abd\n");

        var earlier = DateTime.UtcNow.AddMinutes(-5);
        foreach (var file in new[] { "lines.txt", "lines.text", "data.bin", "staged.txt", "same.txt", "tracked.txt" })
        {
            File.SetLastWriteTimeUtc(Path.Combine(repo, file), earlier);
        }

        var state = await Read(repo);

        await Assert.That(state.Files).IsEquivalentTo([new GitFile("same.txt", GitChange.Modified)]);
    }

    // Read from packs as git gc leaves them, deltas and all, its commit graph walked for ahead,
    // behind and the tag, and from an index of version 4, each path only what differs from the
    // one before.
    [Test]
    public async Task ReadsAPackedRepository()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Directory.CreateDirectory(Path.Combine(repo, "src", "deep"));
        for (var index = 0; index < 5; index++)
        {
            var lines = string.Join('\n', Enumerable.Range(0, 200).Select(_ => $"line {_} of {index}"));
            await File.WriteAllTextAsync(Path.Combine(repo, "src", "deep", "file.txt"), lines);
            await File.WriteAllTextAsync(Path.Combine(repo, "src", $"other{index}.txt"), lines);
            CodeCommandsTests.Scratch.Git(repo, "add", ".");
            CodeCommandsTests.Scratch.Git(repo, "commit", "-m", $"Commit {index}");
            if (index == 1)
            {
                CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "v1.0", "-m", "One");
            }
        }

        CodeCommandsTests.Scratch.Git(repo, "push");
        CodeCommandsTests.Scratch.Git(repo, "reset", "--hard", "HEAD~1");
        Commit(repo, "ahead.txt");
        CodeCommandsTests.Scratch.Git(repo, "gc", "--quiet");
        CodeCommandsTests.Scratch.Git(repo, "update-index", "--index-version", "4");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "other0.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "deep", "new.txt"), "new");
        CodeCommandsTests.Scratch.Git(repo, "rm", "--cached", "-q", "src/other2.txt");

        var state = await Read(repo);

        await Assert.That(state with { Files = [] }).IsEqualTo(new GitState("main", Head(repo), 1, 1, []) with { Upstream = true, Tag = "v1.0" });
        await Assert.That(state.Files)
            .IsEquivalentTo<IReadOnlyList<GitFile>, GitFile>(
                [
                    new("src/deep/new.txt", GitChange.Untracked),
                    new("src/other0.txt", GitChange.Modified),
                    new("src/other2.txt", GitChange.Deleted),
                    new("src/other2.txt", GitChange.Untracked)
                ],
                CollectionOrdering.Matching);
    }

    // As git ignores: a pattern undone by a !, a .gitignore in a folder for what is in it, one
    // anchored to the top, ** across folders, info/exclude. A folder of nothing but what is
    // ignored isn't listed; one with a repository in it is, and so is a repository itself.
    [Test]
    public async Task IgnoresAsGitDoes()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.log\n!keep.log\nbuild/\n/top.txt\ndocs/**/*.tmp\n[Oo]bj/\n");
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        await File.WriteAllTextAsync(Path.Combine(repo, "sub", ".gitignore"), "*.cache\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "sub", "kept.txt"), "kept");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Ignores");
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "info", "exclude"), "excluded.txt\n");

        foreach (var file in new[]
                 {
                     "a.log", "keep.log", "build/out.dll", "top.txt", "sub/top.txt", "sub/y.cache", "sub/z.txt",
                     "only/a.log", "only/build/x.txt", "only/.keep.log", "docs/a/b/c.tmp", "docs/d.txt", "excluded.txt",
                     "Obj/x.txt", "sub/obj/y.txt", "holds/inner/keep.log"
                 })
        {
            var path = Path.Combine(repo, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file);
        }

        var nested = Path.Combine(repo, "nested");
        Directory.CreateDirectory(nested);
        CodeCommandsTests.Scratch.Git(nested, "init", "-q");
        Directory.CreateDirectory(Path.Combine(repo, "empty"));
        var inside = Path.Combine(repo, "outer", "repo");
        Directory.CreateDirectory(inside);
        CodeCommandsTests.Scratch.Git(inside, "init", "-q");

        var state = await Read(repo);

        await Assert.That(state.Files.Select(_ => $"{_.Change} {_.Path}"))
            .IsEquivalentTo(
                [
                    "Untracked docs",
                    "Untracked holds",
                    "Untracked keep.log",
                    "Untracked nested",
                    "Untracked outer",
                    "Untracked sub/top.txt",
                    "Untracked sub/z.txt"
                ],
                CollectionOrdering.Matching);
    }

    // As status.showUntrackedFiles says: all lists each file, no lists none.
    [Test]
    public async Task ListsUntrackedFilesAsConfigured()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Directory.CreateDirectory(Path.Combine(repo, "New", "Deeper"));
        await File.WriteAllTextAsync(Path.Combine(repo, "New", "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(repo, "New", "Deeper", "two.txt"), "two");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");

        CodeCommandsTests.Scratch.Git(repo, "config", "status.showUntrackedFiles", "all");
        var all = await Read(repo);
        CodeCommandsTests.Scratch.Git(repo, "config", "status.showUntrackedFiles", "no");
        var none = await Read(repo);

        await Assert.That(all.Files.Select(_ => _.Path)).IsEquivalentTo(["New/Deeper/two.txt", "New/one.txt", "tracked.txt"], CollectionOrdering.Matching);
        await Assert.That(none.Files.Select(_ => _.Path)).IsEquivalentTo(["tracked.txt"]);
    }

    // Staged as a rename with lines changed, a file is still one renamed, git finding the two
    // alike enough; changed beyond that, it is one deleted and one added. A rename of a folder
    // renames each file in it.
    [Test]
    public async Task ReadsRenamesWithChanges()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var lines = Enumerable.Range(0, 40).Select(_ => $"Line number {_}").ToList();
        await File.WriteAllTextAsync(Path.Combine(repo, "alike.txt"), string.Join('\n', lines));
        await File.WriteAllTextAsync(Path.Combine(repo, "unlike.txt"), string.Join('\n', lines));
        Directory.CreateDirectory(Path.Combine(repo, "folder"));
        await File.WriteAllTextAsync(Path.Combine(repo, "folder", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(repo, "folder", "b.txt"), "b");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Lines");
        Directory.CreateDirectory(Path.Combine(repo, "moved"));
        CodeCommandsTests.Scratch.Git(repo, "mv", "alike.txt", "moved/alike2.txt");
        CodeCommandsTests.Scratch.Git(repo, "mv", "unlike.txt", "unlike2.txt");
        CodeCommandsTests.Scratch.Git(repo, "mv", "folder", "renamed");
        lines[3] = "Line three changed";
        await File.WriteAllTextAsync(Path.Combine(repo, "moved", "alike2.txt"), string.Join('\n', lines));
        await File.WriteAllTextAsync(Path.Combine(repo, "unlike2.txt"), "Nothing like it");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");

        var state = await Read(repo);

        await Assert.That(state.Files.Select(_ => $"{_.Change} {_.Path}"))
            .IsEquivalentTo(
                [
                    "Renamed moved/alike2.txt",
                    "Renamed renamed/a.txt",
                    "Renamed renamed/b.txt",
                    "Deleted unlike.txt",
                    "Added unlike2.txt"
                ],
                CollectionOrdering.Matching);
    }

    // Of tags on one commit, git describe takes an annotated one over light ones, and the first
    // by name of light ones; going back, the nearest of the first ten it finds.
    [Test]
    public async Task ReadsTheTagDescribeChooses()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "tag", "b-light");
        CodeCommandsTests.Scratch.Git(repo, "tag", "a-light");
        var light = (await Read(repo)).Tag;

        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "z-annotated", "-m", "Annotated");
        var annotated = (await Read(repo)).Tag;

        for (var index = 0; index < 12; index++)
        {
            Commit(repo, $"file{index}.txt");
            if (index % 2 == 0)
            {
                CodeCommandsTests.Scratch.Git(repo, "tag", $"t{index}");
            }
        }

        Commit(repo, "last.txt");
        CodeCommandsTests.Scratch.Git(repo, "pack-refs", "--all");
        var back = (await Read(repo)).Tag;

        await Assert.That(light).IsEqualTo("a-light");
        await Assert.That(annotated).IsEqualTo("z-annotated");
        await Assert.That(back).IsEqualTo("t10");
    }

    // A submodule is changed only where it has another commit checked out: what is uncommitted
    // inside one is its own, as --ignore-submodules=dirty has it.
    [Test]
    public async Task ReadsASubmoduleAtAnotherCommit()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        code.CreateRepo("Library");
        CodeCommandsTests.Scratch.Git(repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", code.Origin("Library"), "library");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Library");
        var library = Path.Combine(repo, "library");
        await File.WriteAllTextAsync(Path.Combine(library, "tracked.txt"), "dirty");
        var dirty = await Read(repo);

        // Read itself, its git folder in the repository's, naming it as its working tree.
        var inside = await Read(library);
        await Assert.That(inside.Files).IsEquivalentTo([new GitFile("tracked.txt", GitChange.Modified)]);

        CodeCommandsTests.Scratch.Git(library, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-am", "Moved on");
        var moved = await Read(repo);

        await Assert.That(dirty.Files).IsEmpty();
        await Assert.That(moved.Files).IsEquivalentTo([new GitFile("library", GitChange.Modified)]);
    }

    // A file deleted where a folder now is, a folder of files deleted, and one staged as deleted
    // that is in the working tree still.
    [Test]
    public async Task ReadsWhatWasAFile()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Directory.CreateDirectory(Path.Combine(repo, "gone"));
        await File.WriteAllTextAsync(Path.Combine(repo, "gone", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(repo, "gone", "b.txt"), "b");
        Commit(repo, "kept.txt");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Files");
        Directory.Delete(Path.Combine(repo, "gone"), true);
        File.Delete(Path.Combine(repo, "tracked.txt"));
        Directory.CreateDirectory(Path.Combine(repo, "tracked.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt", "inside.txt"), "inside");
        CodeCommandsTests.Scratch.Git(repo, "rm", "--cached", "-q", "kept.txt");

        var state = await Read(repo);
        CodeCommandsTests.Scratch.Git(repo, "config", "status.showUntrackedFiles", "all");
        var all = await Read(repo);

        await Assert.That(state.Files.Select(_ => $"{_.Change} {_.Path}"))
            .IsEquivalentTo(
                [
                    "Deleted gone/a.txt",
                    "Deleted gone/b.txt",
                    "Deleted kept.txt",
                    "Untracked kept.txt",
                    "Deleted tracked.txt"
                ],
                CollectionOrdering.Matching);
        await Assert.That(all.Files.Select(_ => _.Path)).Contains("tracked.txt/inside.txt");
    }

    // A file only meant to be added, by git add -N, is one not yet staged.
    [Test]
    public async Task ReadsAFileIntendedToBeAdded()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "intended.txt"), "intended");
        CodeCommandsTests.Scratch.Git(repo, "add", "-N", "intended.txt");

        var state = await Read(repo);

        await Assert.That(state.Files).IsEquivalentTo([new GitFile("intended.txt", GitChange.Modified)]);
    }

    // A file git can only hash by running a filter, LFS's say, is left to git once its times
    // aren't those staged.
    [Test]
    public async Task LeavesAFilterToGit()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "config", "filter.upper.clean", "cat");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "*.up filter=upper\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "file.up"), "content");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Filtered");
        var clean = InProcessState.FromFiles(repo, Cancel.None);

        File.SetLastWriteTimeUtc(Path.Combine(repo, "file.up"), DateTime.UtcNow.AddMinutes(-5));

        await Assert.That(clean).IsEqualTo(GitState.FromGit(repo, Cancel.None));
        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
        await Assert.That(InProcessState.FromFaster(repo, Cancel.None)).IsEqualTo(GitState.FromGit(repo, Cancel.None));
    }

    // By both readers, which must agree.
    static async Task<GitState> Read(string repo)
    {
        var git = GitState.FromGit(repo, Cancel.None);
        var inProcess = InProcessState.FromFiles(repo, Cancel.None);
        await Assert.That(git).IsNotNull();
        await Assert.That(inProcess).IsEqualTo(git);
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
