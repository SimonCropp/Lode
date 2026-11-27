/// <summary>
/// Repositories read in process where they are as git writes them, and left to git where they
/// have what isn't read here as git would: a format, extension or ref storage newer than is
/// known, history rewritten by grafts or replacements, refs that are corrupt.
/// </summary>
public class RepositoryTests
{
    static void Config(string repo, params string[] arguments) =>
        CodeCommandsTests.Scratch.Git(repo, ["config", .. arguments]);

    // A format newer than is known, or extensions only git knows, which in format 0 git ignores
    // but for those that change what the repository is.
    [Test]
    [Arguments("2", null, null)]
    [Arguments("1", "extensions.somethingnew", "true")]
    [Arguments("0", "extensions.partialclone", "origin")]
    [Arguments("1", "extensions.objectformat", "sha256")]
    [Arguments("1", "extensions.refstorage", "reftable")]
    public async Task LeavesFormatsItDoesntKnowToGit(string version, string? key, string? value)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Config(repo, "core.repositoryformatversion", version);
        if (key != null)
        {
            Config(repo, key, value!);
        }

        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
    }

    // In format 0 an extension git doesn't know changes nothing; ones that say what it is now,
    // SHA-1 and files, are read.
    [Test]
    [Arguments("0", "extensions.somethingnew", "true")]
    [Arguments("1", "extensions.objectformat", "sha1")]
    [Arguments("1", "extensions.refstorage", "files")]
    [Arguments("1", "extensions.noop", "true")]
    [Arguments("1", "extensions.preciousobjects", "true")]
    [Arguments("1", "extensions.worktreeconfig", "true")]
    public async Task ReadsFormatsItKnows(string version, string key, string value)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Config(repo, "core.repositoryformatversion", version);
        Config(repo, key, value);

        await GitRun.Agree(repo);
    }

    // Made by git with SHA-256 objects, or reftable refs.
    [Test]
    [Arguments("--object-format=sha256")]
    [Arguments("--ref-format=reftable")]
    public async Task LeavesNewerRepositoriesToGit(string format)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.At("New");
        Directory.CreateDirectory(repo);
        CodeCommandsTests.Scratch.Git(repo, "init", "-q", format);
        await File.WriteAllTextAsync(Path.Combine(repo, "file.txt"), "file");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "First");

        await GitRun.LeftToGit(repo);
    }

    // Grafts and replacements, loose or packed, have git read history otherwise than it is
    // written, which isn't followed here; without core.useReplaceRefs git reads it as written.
    [Test]
    public async Task LeavesRewrittenHistoryToGit()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "second");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-am", "Second");
        var first = GitRun.Output(repo, "rev-parse", "HEAD~1");
        var second = GitRun.Output(repo, "rev-parse", "HEAD");
        CodeCommandsTests.Scratch.Git(repo, "replace", second, first);
        await GitRun.LeftToGit(repo);
        await Assert.That(InProcessHistory.Read(repo, 0, 10, Cancel.None)).IsNull();
        await Assert.That(InProcessHistory.Unpushed(repo, Cancel.None)).IsNull();
        await Assert.That(InProcessHistory.UnpushedFaster(repo, Cancel.None)).IsEquivalentTo(GitHistory.Unpushed(repo, Cancel.None));
        await Assert.That(InProcessHistory.ReadFaster(repo, 0, 10, Cancel.None)!.Count).IsEqualTo(GitHistory.Read(repo, 0, 10, Cancel.None)!.Count);

        CodeCommandsTests.Scratch.Git(repo, "pack-refs", "--all");
        await GitRun.LeftToGit(repo);

        Config(repo, "core.useReplaceRefs", "false");
        await GitRun.Agree(repo);

        CodeCommandsTests.Scratch.Git(repo, "replace", "-d", second);
        Config(repo, "--unset", "core.useReplaceRefs");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "grafts"), $"{second}\n");
        await GitRun.LeftToGit(repo);
    }

    // A working tree elsewhere than core.worktree says, or a bare repository with files beside
    // it, isn't what is read here.
    [Test]
    [Arguments("core.worktree", "../elsewhere")]
    [Arguments("core.bare", "true")]
    public async Task LeavesWorkingTreesElsewhereToGit(string key, string value)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Config(repo, key, value);

        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
    }

    // A HEAD, ref or packed-refs line that is neither a ref nor an id; refs that name each other
    // round and round; a .git file that names no git folder.
    [Test]
    [Arguments("head")]
    [Arguments("ref")]
    [Arguments("loop")]
    [Arguments("packed")]
    [Arguments("gitfile")]
    public async Task RefusesCorruptRefs(string breaking)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var git = Path.Combine(repo, ".git");
        switch (breaking)
        {
            case "head":
                await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "neither\n");
                break;
            case "ref":
                await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "main"), "not an id\n");
                break;
            case "loop":
                await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "main"), "ref: refs/heads/other\n");
                await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "other"), "ref: refs/heads/main\n");
                break;
            case "packed":
                CodeCommandsTests.Scratch.Git(repo, "pack-refs", "--all");
                await File.AppendAllTextAsync(Path.Combine(git, "packed-refs"), "short line\n");
                break;
            case "gitfile":
                var moved = scratch.At("moved.git");
                Directory.Move(git, moved);
                await File.WriteAllTextAsync(git, $"elsewhere: {moved}\n");
                break;
        }

        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
    }

    // Packed refs with peeled tags, loose ones over them, a lock file beside a tag, which git
    // is writing, and tags in folders.
    [Test]
    public async Task ReadsRefsLooseAndPacked()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "release/one", "-m", "One");
        CodeCommandsTests.Scratch.Git(repo, "tag", "light");
        CodeCommandsTests.Scratch.Git(repo, "pack-refs", "--all");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "second");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-am", "Second");
        CodeCommandsTests.Scratch.Git(repo, "tag", "-f", "light");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "refs", "tags", "light.lock"), "being written");

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Tag).IsEqualTo("light");
    }

    // The branch a branch tracks: one of origin's by its fetch refspec, one of its own with
    // remote ".", by its name or its whole ref, worktree's own refs among them; none where a
    // negative refspec leaves it out, no refspec fetches it, or no remote is named.
    [Test]
    [Arguments(".", "refs/heads/base", null)]
    [Arguments(".", "base", null)]
    [Arguments(".", "refs/worktree/base", null)]
    [Arguments("origin", "refs/heads/main", "^refs/heads/main")]
    [Arguments("origin", "refs/heads/main", "refs/heads/main:refs/remotes/origin/main")]
    [Arguments("origin", "refs/heads/main", "refs/heads/main")]
    [Arguments("origin", "refs/heads/main", "refs/tags/*:refs/remotes/origin/tags/*")]
    [Arguments("origin", "refs/heads/main", "+refs/heads/ma*:refs/remotes/origin/x*")]
    [Arguments("origin", "refs/heads/main", "^refs/heads/other|+refs/heads/*:refs/remotes/origin/*")]
    [Arguments("origin", "refs/heads/main", "refs/heads/other:refs/remotes/origin/other")]
    [Arguments(null, "refs/heads/main", null)]
    public async Task TracksAsGitDoes(string? remote, string merge, string? fetch)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "branch", "base");
        CodeCommandsTests.Scratch.Git(repo, "update-ref", "refs/worktree/base", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "ahead");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-am", "Ahead");
        Config(repo, "--unset-all", "branch.main.remote");
        if (remote != null)
        {
            Config(repo, "branch.main.remote", remote);
        }

        Config(repo, "branch.main.merge", merge);
        if (fetch != null)
        {
            var refspecs = fetch.Split('|');
            Config(repo, "--replace-all", "remote.origin.fetch", refspecs[0]);
            foreach (var refspec in refspecs[1..])
            {
                Config(repo, "--add", "remote.origin.fetch", refspec);
            }
        }

        await GitRun.Agree(repo);
        await Assert.That(InProcessHistory.Unpushed(repo, Cancel.None)!.Order()).IsEquivalentTo(GitHistory.Unpushed(repo, Cancel.None).Order(), CollectionOrdering.Matching);
    }

    // A submodule is changed where it has another commit checked out, read from its own git
    // folder: detached, or on a branch whose ref is loose or packed, or with no commits yet. A
    // worktree of the repository itself, staged as a submodule, by its own HEAD.
    [Test]
    public async Task ReadsSubmodulesHeadsAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        scratch.CreateRepo("Library");
        CodeCommandsTests.Scratch.Git(repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", scratch.Origin("Library"), "library");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", "Library");
        var library = Path.Combine(repo, "library");
        CodeCommandsTests.Scratch.Git(library, "switch", "-q", "-c", "work");
        await GitRun.Agree(repo);

        await File.WriteAllTextAsync(Path.Combine(library, "tracked.txt"), "moved on");
        CodeCommandsTests.Scratch.Git(library, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-am", "Moved on");
        await GitRun.Agree(repo);

        CodeCommandsTests.Scratch.Git(library, "pack-refs", "--all");
        await GitRun.Agree(repo);

        File.Delete(Path.Combine(GitRepository.GitDirOf(library)!, "packed-refs"));
        await GitRun.Agree(repo);

        CodeCommandsTests.Scratch.Git(library, "switch", "-q", "--orphan", "empty");
        await GitRun.Agree(repo);

        // A worktree, its HEAD in the repository's git folder, its branches in the common one.
        CodeCommandsTests.Scratch.Git(repo, "worktree", "add", "-q", "-b", "inner", Path.Combine(repo, "inner"));
        var head = GitRun.Output(repo, "rev-parse", "HEAD~1");
        CodeCommandsTests.Scratch.Git(repo, "update-index", "--add", "--cacheinfo", $"160000,{head},inner");
        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files.Select(_ => _.Path)).Contains("inner");
        await Assert.That(GitRepository.HeadOf(scratch.At("NotThere"))).IsNull();
    }

    // A worktree's HEAD may be any ref of its own; a submodule's HEAD naming one not under refs/
    // is read from its git folder.
    [Test]
    public async Task ReadsHeadsOfEveryKind()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var head = GitRun.Output(repo, "rev-parse", "HEAD");
        var git = Path.Combine(repo, ".git");
        await File.WriteAllTextAsync(Path.Combine(git, "PSEUDO_HEAD"), head + "\n");
        await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "ref: PSEUDO_HEAD\n");

        ObjectId.TryParse(head, out var id);
        await Assert.That(GitRepository.HeadOf(repo)).IsEqualTo(id);

        await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "garbage\n");
        await Assert.That(GitRepository.HeadOf(repo)).IsNull();

        await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "ref: refs/heads/gone\n");
        await Assert.That(GitRepository.HeadOf(repo)).IsNull();

        CodeCommandsTests.Scratch.Git(repo, "pack-refs", "--all");
        await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "ref: refs/heads/gone\n");
        await Assert.That(GitRepository.HeadOf(repo)).IsNull();

        await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "ref: refs/heads/a\n");
        await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "a"), "ref: refs/heads/b\n");
        await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "b"), "ref: refs/heads/c\n");
        await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "c"), "ref: refs/heads/d\n");
        await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "d"), "ref: refs/heads/e\n");
        await File.WriteAllTextAsync(Path.Combine(git, "refs", "heads", "e"), "ref: refs/heads/main\n");
        await Assert.That(GitRepository.HeadOf(repo)).IsNull();
    }
}
