/// <summary>
/// The git commands, run for real against throwaway repositories in the temp folder. A bare
/// repository stands in for origin, so nothing leaves the machine.
/// </summary>
public class CodeCommandsTests
{
    [Test]
    public async Task FindsReposUpToTwoLevelsDown()
    {
        using var code = new Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));
        Directory.CreateDirectory(code.At("Group", "Deeper", "Three", ".git"));
        Directory.CreateDirectory(code.At("NotARepo"));
        Directory.CreateDirectory(code.At("Worktree"));
        await File.WriteAllTextAsync(code.At("Worktree", ".git"), "gitdir: elsewhere");

        var repos = CodeCommands.FindRepos(code.Root);

        await Assert.That(repos.Select(code.Relative)).IsEquivalentTo(["Group/Two", "One", "Worktree"]);
    }

    [Test]
    public async Task ARepoIsJustItself()
    {
        using var code = new Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("One", "Nested", ".git"));

        var repos = CodeCommands.FindRepos(code.At("One"));

        await Assert.That(repos.Select(code.Relative)).IsEquivalentTo(["One"]);
    }

    // A folder inside a repository, src say, is that repository, as far up as the code folder:
    // not one above it, such as a home folder kept in git.
    [Test]
    public async Task AFolderInARepoIsThatRepo()
    {
        using var code = new Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("One", "src", "Inner", ".git"));
        Directory.CreateDirectory(code.At("Code", "Two", "src"));
        Directory.CreateDirectory(code.At(".git"));

        var inRepo = CodeCommands.FindRepos(code.At("One", "src"), code.Root);
        var deeper = CodeCommands.FindRepos(code.At("One", "src", "Inner", "lib"), code.Root);
        var aboveTheCodeFolder = CodeCommands.FindRepos(code.At("Code", "Two", "src"), code.At("Code"));

        await Assert.That(inRepo.Select(code.Relative)).IsEquivalentTo(["One"]);
        await Assert.That(deeper.Select(code.Relative)).IsEquivalentTo(["One/src/Inner"]);
        await Assert.That(aboveTheCodeFolder).IsEmpty();
    }

    // Several folders: the repositories of each, once each, in order.
    [Test]
    public async Task FindsReposAcrossFolders()
    {
        using var code = new Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));
        Directory.CreateDirectory(code.At("Group", "Three", ".git"));

        var repos = CodeCommands.FindRepos([code.At("One"), code.At("Group"), code.At("Group", "Two")]);

        await Assert.That(repos.Select(code.Relative)).IsEquivalentTo(["Group/Three", "Group/Two", "One"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OutermostLeavesOutFoldersInsideOthers()
    {
        var folders = CodeCommands.Outermost([@"C:\Code\Lode", @"C:\Code\Lode\src", @"C:\Code\Lodestar", @"C:\Code\lode"]);

        await Assert.That(folders).IsEquivalentTo([@"C:\Code\Lode", @"C:\Code\Lodestar"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CommonFolderHoldsThemAll()
    {
        await Assert.That(CodeCommands.CommonFolder([@"C:\Code\One", @"C:\Code\Two"])).IsEqualTo(@"C:\Code");
        await Assert.That(CodeCommands.CommonFolder([@"C:\Code\One", @"C:\Code\Group\Two"])).IsEqualTo(@"C:\Code");
        await Assert.That(CodeCommands.CommonFolder([@"C:\Code\Group\One", @"C:\Code\Group\Two"])).IsEqualTo(@"C:\Code\Group");
    }

    // The files as they were go to the Recycle Bin first, here a stand-in, the changed one and
    // the new one, so either can be got back.
    [Test]
    public async Task DiscardRestoresAndCleans()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");
        var bin = new Bin(code);

        var results = await CodeCommands.Discard(CodeCommands.DiscardTargets([repo]), bin.Recycle);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "untracked.txt"))).IsFalse();
        await Assert.That(bin.Recycled).IsEquivalentTo(["Repo/tracked.txt", "Repo/untracked.txt"], CollectionOrdering.Matching);
        await Assert.That(bin.Read("Repo/tracked.txt")).IsEqualTo("changed");
        await Assert.That(bin.Read("Repo/untracked.txt")).IsEqualTo("new");
        await Assert.That(results.Single().Output).StartsWith("Moved 2 files as they were to the Recycle Bin\n");
    }

    // Every uncommitted change, as its prompt says: staged ones too, which the next Push would
    // otherwise commit, and untracked folders as well as files, each file going to the Recycle
    // Bin. Not what git ignores.
    [Test]
    public async Task DiscardTakesStagedChangesAndUntrackedFolders()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "ignored.txt");
        Scratch.Git(repo, "add", ".gitignore");
        Scratch.Git(repo, "commit", "-m", "Ignore");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "staged");
        await File.WriteAllTextAsync(Path.Combine(repo, "added.txt"), "added");
        Scratch.Git(repo, "add", ".");
        Directory.CreateDirectory(Path.Combine(repo, "folder"));
        await File.WriteAllTextAsync(Path.Combine(repo, "folder", "untracked.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(repo, "ignored.txt"), "kept");
        var bin = new Bin(code);

        var results = await CodeCommands.Discard(CodeCommands.DiscardTargets([repo]), bin.Recycle);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "added.txt"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(repo, "folder"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(repo, "ignored.txt"))).IsTrue();
        await Assert.That(bin.Recycled).IsEquivalentTo(["Repo/added.txt", "Repo/folder/untracked.txt", "Repo/tracked.txt"], CollectionOrdering.Matching);
        await Assert.That(bin.Read("Repo/tracked.txt")).IsEqualTo("staged");
    }

    // A folder inside a repository is discarded alone, as Purge purges it alone: what is
    // changed, staged, or new in it, each file as it was going to the Recycle Bin first. The
    // changes in the folder beside it stay as they are, staged ones staged.
    [Test]
    public async Task DiscardingAFolderInARepo()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var one = Path.Combine(repo, "src", "One");
        var two = Path.Combine(repo, "src", "Two");
        Directory.CreateDirectory(one);
        Directory.CreateDirectory(two);
        await File.WriteAllTextAsync(Path.Combine(one, "one.txt"), "original");
        await File.WriteAllTextAsync(Path.Combine(two, "two.txt"), "original");
        Scratch.Git(repo, "add", ".");
        Scratch.Git(repo, "commit", "-m", "Folders");
        await File.WriteAllTextAsync(Path.Combine(one, "one.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(one, "added.txt"), "added");
        await File.WriteAllTextAsync(Path.Combine(two, "two.txt"), "changed");
        Scratch.Git(repo, "add", ".");
        await File.WriteAllTextAsync(Path.Combine(one, "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(two, "new.txt"), "new");
        var bin = new Bin(code);

        var targets = CodeCommands.DiscardTargets([one], code.Root);
        var results = await CodeCommands.Discard(targets, bin.Recycle);

        await Assert.That(CodeMenu.DiscardQuestion(targets)).IsEqualTo("Discard every uncommitted change, and delete untracked files, in src\\One in Repo?\n\nThe files as they are now go to the Recycle Bin.");
        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(one, "one.txt"))).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(one, "added.txt"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(one, "new.txt"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(two, "two.txt"))).IsEqualTo("changed");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo("M  src/Two/two.txt\n?? src/Two/new.txt");
        await Assert.That(bin.Recycled).IsEquivalentTo(["Repo/src/One/added.txt", "Repo/src/One/new.txt", "Repo/src/One/one.txt"], CollectionOrdering.Matching);
        await Assert.That(bin.Read("Repo/src/One/one.txt")).IsEqualTo("changed");
        await Assert.That(bin.Read("Repo/src/One/added.txt")).IsEqualTo("added");
        await Assert.That(bin.Read("Repo/src/One/new.txt")).IsEqualTo("new");
    }

    // Files that can't all go to the Recycle Bin, one in use say: nothing is discarded, so
    // nothing is lost.
    [Test]
    public async Task DiscardWithoutTheRecycleBinDiscardsNothing()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");

        var results = await CodeCommands.Discard(CodeCommands.DiscardTargets([repo]), _ => Task.FromResult(false));

        await Assert.That(results.Single().Succeeded).IsFalse();
        await Assert.That(results.Single().Output).IsEqualTo("Not all the files could go to the Recycle Bin, so nothing was discarded. Those that went are there, to restore.");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("changed");
        await Assert.That(File.Exists(Path.Combine(repo, "untracked.txt"))).IsTrue();
    }

    // A folder below the top of a repository is that folder alone, and two in one repository
    // are one discard there, of both; but a folder in a repository picked whole, or in another
    // folder picked, goes with that. A folder holding repositories, as the code folder does, is
    // every one of them, whole.
    [Test]
    public async Task WhatDiscardWorksOn()
    {
        using var code = new Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("One", "src", "Deep"));
        Directory.CreateDirectory(code.At("One", "tests"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", "src"));
        Directory.CreateDirectory(code.At("Group", "Three", ".git"));

        // Each folder relative to the code folder, which is "".
        List<string> Targets(params string[] folders) =>
            CodeCommands.DiscardTargets([.. folders.Select(_ => code.At(_.Replace('/', '\\')))], code.Root)
                .Select(_ => $"{code.Relative(_.Repo)}: {string.Join(", ", _.Folders.Select(code.Relative))}")
                .ToList();

        await Assert.That(Targets("One/src")).IsEquivalentTo(["One: One/src"]);
        await Assert.That(Targets("One/src", "One/tests", "One/src/Deep")).IsEquivalentTo(["One: One/src, One/tests"]);
        await Assert.That(Targets("One/src", "One")).IsEquivalentTo(["One: "]);
        await Assert.That(Targets("Group", "Group/Two/src", "One/tests")).IsEquivalentTo(["Group/Three: ", "Group/Two: ", "One: One/tests"], CollectionOrdering.Matching);
        await Assert.That(Targets("")).IsEquivalentTo(["Group/Three: ", "Group/Two: ", "One: "], CollectionOrdering.Matching);

        // Its question names each folder by where it is in its repository.
        await Assert.That(CodeMenu.DiscardQuestion(CodeCommands.DiscardTargets([code.Root], code.Root)))
            .IsEqualTo("Discard every uncommitted change, and delete untracked files, in 3 repositories (Three, Two, One)?\n\nThe files as they are now go to the Recycle Bin.");
        await Assert.That(CodeMenu.DiscardQuestion(CodeCommands.DiscardTargets([code.At("One", "src"), code.At("One", "tests"), code.At("Group", "Two")], code.Root)))
            .IsEqualTo("Discard every uncommitted change, and delete untracked files, in Two, src in One, tests in One?\n\nThe files as they are now go to the Recycle Bin.");
    }

    // Stands in for the Recycle Bin, so a test run leaves nothing there: each file is moved into
    // a folder of the scratch folder's, under where it was, as the Recycle Bin keeps where each
    // came from.
    sealed class Bin(Scratch code)
    {
        public List<string> Recycled { get; } = [];

        public Task<bool> Recycle(IReadOnlyList<string> paths)
        {
            foreach (var path in paths)
            {
                var relative = code.Relative(path);
                Recycled.Add(relative);
                var inBin = code.At("Bin", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(inBin)!);
                File.Move(path, inBin);
            }

            Recycled.Sort(StringComparer.Ordinal);
            return Task.FromResult(true);
        }

        public string Read(string relative) =>
            File.ReadAllText(code.At("Bin", relative));
    }

    // Git waiting on something that never comes, ssh on a passphrase say, is stopped where it
    // is when cancelled, with whatever it started.
    [Test]
    public async Task CancellingStopsGit()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        using var cancel = new CancelSource(TimeSpan.FromMilliseconds(500));
        var started = Stopwatch.StartNew();

        var result = await Task.Run(
            () => CodeCommands.Git(repo, cancel.Token, network: false, "-c", "alias.hang=!sleep 60", "hang"), cancel.Token);

        await Assert.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(20));
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).EndsWith("Cancelled");
    }

    // A git that changes the repository is not stopped part way, which would leave the index
    // locked: cancelled meanwhile, it finishes, and it is the next one that isn't started.
    [Test]
    public async Task AGitChangingTheRepositoryIsLeftToFinish()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        using var cancel = new CancelSource(TimeSpan.FromMilliseconds(500));

        var result = await Task.Run(() => CodeCommands.GitToItsEnd(repo, cancel.Token, "-c", "alias.slow=!sleep 2", "slow"), cancel.Token);
        var next = CodeCommands.GitToItsEnd(repo, cancel.Token, "status");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(next.Succeeded).IsFalse();
        await Assert.That(next.Output).IsEqualTo("Cancelled");
    }

    // Cancelled, a command goes on to no more repositories.
    [Test]
    public async Task CancelledGoesToNoMoreRepos()
    {
        using var code = new Scratch();
        var one = code.CreateRepo("One");
        var two = code.CreateRepo("Two");
        using var cancel = new CancelSource();

        // Cancelled once the first is done: those running stop, and no more are started.
        string[] repos = [one, two, .. Enumerable.Range(3, CodeCommands.AtOnce).Select(_ => code.CreateRepo($"Repo{_}"))];
        var results = await CodeCommands.Pull(repos, new CancelOnReport(cancel), cancel.Token);

        await Assert.That(results.Count).IsLessThanOrEqualTo(CodeCommands.AtOnce);
        await Assert.That(results.Count).IsLessThan(repos.Length);
    }

    // Cancelled before it starts, it starts nothing, and ends as cancelled.
    [Test]
    public async Task CancelledBeforeStartingStartsNothing()
    {
        using var code = new Scratch();
        var one = code.CreateRepo("One");

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => CodeCommands.Pull([one], cancel: new(canceled: true)));
    }

    // What git wrote, as a terminal shows it: a line written over is gone, and so is what clears
    // the rest of a terminal's line, as origin's lines end. Each line is reported as it comes,
    // those written over too.
    [Test]
    public async Task ReadReportingKeepsWhatATerminalShows()
    {
        var reported = new List<string>();

        var output = CodeCommands.ReadReporting(
            new StringReader(
                "Cloning into 'Lode'...\n" +
                "remote: Counting objects:  50% (1/2)\e[K\r" +
                "remote: Counting objects: 100% (2/2), done.\e[K\r\n" +
                "Receiving objects:  50% (1/2)\r" +
                "Receiving objects: 100% (2/2), done.\n" +
                "\n" +
                "fatal: the last line"),
            reported.Add);

        await Assert.That(output).IsEqualTo(
            "Cloning into 'Lode'...\n" +
            "remote: Counting objects: 100% (2/2), done.\n" +
            "Receiving objects: 100% (2/2), done.\n" +
            "\n" +
            "fatal: the last line");
        await Assert.That(reported).IsEquivalentTo(
            [
                "Cloning into 'Lode'...",
                "remote: Counting objects:  50% (1/2)",
                "remote: Counting objects: 100% (2/2), done.",
                "Receiving objects:  50% (1/2)",
                "Receiving objects: 100% (2/2), done.",
                "fatal: the last line"
            ],
            CollectionOrdering.Matching);
    }

    // Cancels as soon as progress is reported, on the thread reporting it.
    sealed class CancelOnReport(CancelSource cancel) : IProgress<string>
    {
        public void Report(string value) =>
            cancel.Cancel();
    }

    // Going to a remote, ssh is told not to ask, as nobody can answer in a hidden window: it
    // fails rather than waits. Unless the user has an ssh command of their own, which may ask
    // some other way.
    [Test]
    public async Task SshIsToldNotToAskUnlessTheUserHasTheirOwn()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        string[] showSsh = ["-c", "alias.ssh=!echo \"[$GIT_SSH_COMMAND]\"", "ssh"];

        var network = CodeCommands.Git(repo, Cancel.None, network: true, showSsh).Output;
        var local = CodeCommands.Git(repo, Cancel.None, network: false, showSsh).Output;
        Scratch.Git(repo, "config", "core.sshCommand", "ssh -i elsewhere");
        var theirOwn = CodeCommands.Git(repo, Cancel.None, network: true, showSsh).Output;

        await Assert.That(network).IsEqualTo("[ssh -o BatchMode=yes]");
        await Assert.That(local).IsEqualTo("[]");
        await Assert.That(theirOwn).IsEqualTo("[]");
    }

    // Git words what it prints in English, whatever the user's language, since Purge reads its
    // "Would remove" lines. File names still come through as UTF-8.
    [Test]
    public async Task GitSpeaksEnglish()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "St Mary’s ä.txt"), "");

        var locale = CodeCommands.Git(repo, "-c", "alias.locale=!echo \"[$LC_ALL]\"", "locale").Output;
        var clean = CodeCommands.Git(repo, "-c", "core.quotepath=off", "clean", "-n").Output;

        await Assert.That(locale).IsEqualTo("[C]");
        await Assert.That(clean).IsEqualTo("Would remove St Mary’s ä.txt");
    }

    [Test]
    public async Task PushCommitsWithTheMessageAndPushes()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");

        var results = await CodeCommands.Push([repo], "Tidy up");

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s")).IsEqualTo("Tidy up");
    }

    // Nothing to commit still pushes what was committed before.
    [Test]
    public async Task PushWithNothingToCommitStillPushes()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Scratch.Git(repo, "commit", "-am", "Earlier");

        var results = await CodeCommands.Push([repo], "Unused");

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s")).IsEqualTo("Earlier");
    }

    // Push asks for a message only where there is something to commit, a new file included;
    // with none, it only pushes.
    [Test]
    public async Task PushWithoutAMessageOnlyPushes()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Scratch.Git(repo, "commit", "-am", "Earlier");

        await Assert.That(CodeCommands.AnyChanges([repo])).IsFalse();
        var results = await CodeCommands.Push([repo], null);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s")).IsEqualTo("Earlier");

        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "new");
        await Assert.That(CodeCommands.AnyChanges([repo])).IsTrue();
        await Assert.That(CodeMenu.PushOnlyQuestion([repo])).IsEqualTo("Push Repo to origin?\n\nNothing is uncommitted, so only what is committed goes.");
    }

    // New files go in too, as the Changes pane commits them, though not what git ignores, even
    // with nothing else changed.
    [Test]
    public async Task PushCommitsNewFiles()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.log\n");
        Scratch.Git(repo, "add", ".gitignore");
        Scratch.Git(repo, "commit", "-m", "Ignore logs");
        Directory.CreateDirectory(Path.Combine(repo, "Source"));
        await File.WriteAllTextAsync(Path.Combine(repo, "Source", "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(repo, "build.log"), "ignored");

        await Assert.That(CodeCommands.FindUntracked([repo]).NewFiles).IsEqualTo(1);
        var results = await CodeCommands.Push([repo], "Add new");

        await Assert.That(results.Single().Succeeded).IsTrue();
        var origin = code.Origin("Repo");
        await Assert.That(Scratch.Git(origin, "log", "-1", "--format=%s")).IsEqualTo("Add new");
        var files = Scratch.Git(origin, "ls-tree", "-r", "--name-only", "HEAD").Split('\n');
        await Assert.That(files).Contains("Source/new.txt");
        await Assert.That(files).DoesNotContain("build.log");
        await Assert.That(CodeCommands.FindUntracked([repo]).NewFiles).IsEqualTo(0);
    }

    // A repository of its own inside, git doesn't track, a clone or a worktree say: added, it
    // would go in as a link to its commit, which origin hasn't got, and Push send that. It is
    // left out, and said to be, its name taken as it is, not as a pattern, whose brackets would
    // leave out the file beside it too. New files go in as any others, and only they count.
    [Test]
    public async Task PushLeavesOutARepositoryInside()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var inner = Path.Combine(repo, "tools", "Inner [v1]");
        Directory.CreateDirectory(inner);
        Scratch.Git(inner, "init", "--initial-branch=main");
        Scratch.Identify(inner);
        await File.WriteAllTextAsync(Path.Combine(inner, "inner.txt"), "inner");
        Scratch.Git(inner, "add", ".");
        Scratch.Git(inner, "commit", "-m", "Inner");
        await File.WriteAllTextAsync(Path.Combine(repo, "tools", "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(repo, "tools", "Inner v"), "new");

        var untracked = CodeCommands.FindUntracked([repo]);
        var results = await CodeCommands.Push([repo], "Add new");

        await Assert.That(untracked.NewFiles).IsEqualTo(2);
        await Assert.That(untracked.Repos).IsEquivalentTo([inner]);
        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(results.Single().Output).StartsWith("Left out tools/Inner [v1]/, a repository of its own\n");
        var origin = code.Origin("Repo");
        await Assert.That(Scratch.Git(origin, "log", "-1", "--format=%s")).IsEqualTo("Add new");
        var pushed = Scratch.Git(origin, "ls-tree", "-r", "--name-only", "HEAD").Split('\n');
        await Assert.That(pushed).IsEquivalentTo(["tools/Inner v", "tools/new.txt", "tracked.txt"]);
        await Assert.That(Scratch.Git(repo, "ls-tree", "-r", "HEAD")).DoesNotContain("160000");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo("?? \"tools/Inner [v1]/\"");
    }

    // What Push asks says how many new files go in, there being any, and which repositories of
    // their own inside it leaves out, each where it is in its repository, or, of several, from
    // the folder holding them.
    [Test]
    public async Task PushQuestionSaysHowManyNewFiles()
    {
        await Assert.That(CodeMenu.PushQuestion([@"C:\Code\Lode"], 0)).IsEqualTo("Commit all changes in Lode and push to origin.");
        await Assert.That(CodeMenu.PushQuestion([@"C:\Code\Lode"], 1)).IsEqualTo("Commit all changes in Lode, including 1 new file, and push to origin.");
        await Assert.That(CodeMenu.PushQuestion([@"C:\Code\Lode"], 3)).IsEqualTo("Commit all changes in Lode, including 3 new files, and push to origin.");
        await Assert.That(CodeMenu.PushQuestion([@"C:\Code\Lode"], 1, [@"C:\Code\Lode\.claude\worktrees\fix"]))
            .IsEqualTo("Commit all changes in Lode, including 1 new file, and push to origin.\n\nLeaves out .claude\\worktrees\\fix, a repository of its own inside, which git would commit only as a link to its commit.");
        await Assert.That(CodeMenu.PushQuestion([@"C:\Code\Lode", @"C:\Code\Group\Two"], 0, [@"C:\Code\Lode\tools\Clone", @"C:\Code\Group\Two\.claude\worktrees\fix"]))
            .IsEqualTo("Commit all changes in 2 repositories (Lode, Two) and push to origin.\n\nLeaves out 2 repositories of their own inside, which git would commit only as links to their commits: Lode\\tools\\Clone, Group\\Two\\.claude\\worktrees\\fix.");
    }

    // A merge that left a file in conflict: the commit would take it as it is, the markers in
    // it, and Push send that. Nothing is committed.
    [Test]
    public async Task PushLeavesARepositoryWithFilesInConflict()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var other = code.Clone("Repo", "Other");
        await File.WriteAllTextAsync(Path.Combine(other, "tracked.txt"), "theirs");
        Scratch.Git(other, "commit", "-am", "Theirs");
        Scratch.Git(other, "push");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "ours");
        Scratch.Git(repo, "commit", "-am", "Ours");
        Scratch.Git(repo, "fetch");
        var merge = CodeCommands.Git(repo, "merge", "origin/main");

        var results = await CodeCommands.Push([repo], "Tidy up");

        await Assert.That(merge.Succeeded).IsFalse();
        await Assert.That(results.Single().Succeeded).IsFalse();
        await Assert.That(results.Single().Output).Contains("still in conflict");
        await Assert.That(Scratch.Git(repo, "log", "-1", "--format=%s")).IsEqualTo("Ours");
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s")).IsEqualTo("Theirs");
    }

    // On a commit, not a branch, as part way through a rebase: the commit would be one no
    // branch has, and there is nowhere to push it.
    [Test]
    public async Task PushLeavesARepositoryWithNoBranchCheckedOut()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "--detach");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");

        var results = await CodeCommands.Push([repo], "Tidy up");

        await Assert.That(results.Single().Succeeded).IsFalse();
        await Assert.That(results.Single().Output).Contains("No branch is checked out");
        await Assert.That(Scratch.Git(repo, "log", "-1", "--format=%s")).IsEqualTo("Initial");
    }

    // A branch made here, as Lode makes one, tracks none: git's push, as push.default=simple
    // has it, would refuse it, having nowhere set to push to, once the commit was made. It goes
    // to a branch of its own name on origin, which it tracks from then on, and Push says so,
    // whether it commits first or only pushes.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PushPublishesABranchTrackingNone(bool committing)
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "config", "push.autoSetupRemote", "false");
        Scratch.Git(repo, "switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        if (!committing)
        {
            Scratch.Git(repo, "commit", "-am", "Feature");
        }

        var result = (await CodeCommands.Push([repo], committing ? "Feature" : null)).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).EndsWith("Published feature to origin, as origin/feature, which it tracks from now on.");
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s", "feature")).IsEqualTo("Feature");
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "feature")).IsEqualTo(Scratch.Git(repo, "rev-parse", "HEAD"));
        await Assert.That(Scratch.Git(repo, "rev-parse", "--abbrev-ref", "feature@{upstream}")).IsEqualTo("origin/feature");
    }

    [Test]
    public async Task PullBringsInWhatOriginHas()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var other = code.Clone("Repo", "Other");
        await File.WriteAllTextAsync(Path.Combine(other, "tracked.txt"), "from elsewhere");
        Scratch.Git(other, "commit", "-am", "Elsewhere");
        Scratch.Git(other, "push", "origin");

        var results = await CodeCommands.Pull([repo]);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("from elsewhere");
    }

    // The repository and origin each changed the same line since they last agreed, as a
    // package bumped both here and on origin is: a pull would conflict, merging or rebasing as
    // pull.rebase says.
    static string Conflicting(Scratch code, string pullRebase)
    {
        var repo = code.CreateRepo("Repo");
        var other = code.Clone("Repo", "Other");
        File.WriteAllText(Path.Combine(other, "tracked.txt"), "theirs");
        Scratch.Git(other, "commit", "-am", "Theirs");
        Scratch.Git(other, "push");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "ours");
        Scratch.Git(repo, "commit", "-am", "Ours");
        Scratch.Git(repo, "config", "pull.rebase", pullRebase);
        return repo;
    }

    // The merge or rebase the pull began is undone, so the repository isn't left part way
    // through one, its Push refused till it is settled.
    [Test]
    [Arguments("false", "merge")]
    [Arguments("true", "rebase")]
    public async Task PullUndoesAPullThatWouldConflict(string pullRebase, string unfinished)
    {
        using var code = new Scratch();
        var repo = Conflicting(code, pullRebase);
        var ours = Scratch.Git(repo, "rev-parse", "HEAD");

        var result = (await CodeCommands.Pull([repo])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains($"Pulling origin/main would conflict, so nothing was pulled. The {unfinished} was undone: pull it yourself, settling the conflicts.");
        await Assert.That(result.Output).EndsWith("In conflict:\ntracked.txt");
        await Assert.That(CodeCommands.Unfinished(repo)).IsNull();
        await Assert.That(Scratch.Git(repo, "branch", "--show-current")).IsEqualTo("main");
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(ours);
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo("");
    }

    // Nothing goes to origin once the pull is undone.
    [Test]
    public async Task PullThenPushPushesNothingWhenThePullWouldConflict()
    {
        using var code = new Scratch();
        var repo = Conflicting(code, "false");
        var origin = Scratch.Git(code.Origin("Repo"), "rev-parse", "main");

        var result = (await CodeCommands.PullThenPush([repo])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("Pulling origin/main would conflict, so nothing was pulled or pushed. The merge was undone");
        await Assert.That(CodeCommands.Unfinished(repo)).IsNull();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(origin);
    }

    // Part way through a merge of the user's own, its conflicts settled, waiting to be
    // committed: undoing the pull would undo that too, so there is no pull, and the merge stays
    // as it was.
    [Test]
    public async Task PullLeavesAMergeNotFinished()
    {
        using var code = new Scratch();
        var repo = Conflicting(code, "false");
        Scratch.Git(repo, "fetch");
        var merge = CodeCommands.Git(repo, "merge", "origin/main");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "settled");
        Scratch.Git(repo, "add", "tracked.txt");

        var result = (await CodeCommands.Pull([repo])).Single();

        await Assert.That(merge.Succeeded).IsFalse();
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("It is part way through a merge, so nothing was pulled. Settle its conflicts in Changes and commit it, or abort it there.");
        await Assert.That(CodeCommands.Unfinished(repo)).IsEqualTo("merge");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("settled");
    }

    // Push refusing files in conflict says what to do about the merge they are from.
    [Test]
    public async Task PushSaysHowToSettleAMerge()
    {
        using var code = new Scratch();
        var repo = Conflicting(code, "false");
        Scratch.Git(repo, "fetch");
        CodeCommands.Git(repo, "merge", "origin/main");

        var result = (await CodeCommands.Push([repo], "Tidy up")).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("Files in it are still in conflict, from a merge not finished, so nothing was committed or pushed. Settle its conflicts in Changes and commit it, or abort it there.");
    }

    // Changes has no rebase to go on with, so a rebase is left to git.
    [Test]
    public async Task PushSaysHowToSettleARebase()
    {
        using var code = new Scratch();
        var repo = Conflicting(code, "true");
        Scratch.Git(repo, "fetch");
        CodeCommands.Git(repo, "rebase", "origin/main");

        var result = (await CodeCommands.Push([repo], "Tidy up")).Single();

        await Assert.That(CodeCommands.Unfinished(repo)).IsEqualTo("rebase");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("Files in it are still in conflict, from a rebase not finished, so nothing was committed or pushed. Go on with it once its conflicts are settled, git rebase --continue, or give it up, git rebase --abort.");
    }

    // A fork's Fetch learns what upstream has too, as GitHub Desktop's does.
    [Test]
    public async Task FetchInAForkFetchesUpstreamToo()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        var upstream = code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var results = await CodeCommands.Fetch([fork]);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(fork, "rev-parse", "upstream/main")).IsEqualTo(upstream);
    }

    [Test]
    public async Task AForkIsARepositoryWithAnUpstream()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Fork");
        var repo = code.CreateRepo("Repo");

        await Assert.That(CodeCommands.IsFork(fork)).IsTrue();
        await Assert.That(CodeCommands.IsFork(repo)).IsFalse();
    }

    // Upstream's commits come in, and go to origin: the fork's main is upstream's.
    [Test]
    public async Task SyncForkBringsInUpstreamAndPushesToOrigin()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        var upstream = code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(fork, "rev-parse", "HEAD")).IsEqualTo(upstream);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(upstream);
    }

    // A branch with commits of its own gets a merge, as GitHub Desktop's Update from upstream
    // makes, and that goes to origin.
    [Test]
    public async Task SyncForkMergesIntoABranchWithCommitsOfItsOwn()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        await File.WriteAllTextAsync(Path.Combine(fork, "mine.txt"), "mine");
        Scratch.Git(fork, "add", "mine.txt");
        Scratch.Git(fork, "commit", "-m", "Mine");
        code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(File.Exists(Path.Combine(fork, "mine.txt"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(fork, "upstream.txt"))).IsTrue();
        // The commit, then its two parents.
        await Assert.That(Scratch.Git(fork, "rev-list", "--parents", "-1", "HEAD").Split(' ').Length).IsEqualTo(3);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(Scratch.Git(fork, "rev-parse", "HEAD"));
    }

    // Both changed the same line: the merge is undone, so the fork isn't left part way through
    // one, and nothing goes to origin.
    [Test]
    public async Task SyncForkUndoesAMergeThatWouldConflict()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        await File.WriteAllTextAsync(Path.Combine(fork, "tracked.txt"), "mine");
        Scratch.Git(fork, "commit", "-am", "Mine");
        var mine = Scratch.Git(fork, "rev-parse", "HEAD");
        var origin = Scratch.Git(code.Origin("Repo"), "rev-parse", "main");
        code.CommitUpstream("Repo", "tracked.txt", "theirs");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("would conflict");
        await Assert.That(result.Output).Contains("tracked.txt");
        await Assert.That(File.Exists(Path.Combine(fork, ".git", "MERGE_HEAD"))).IsFalse();
        await Assert.That(Scratch.Git(fork, "status", "--porcelain")).IsEqualTo("");
        await Assert.That(Scratch.Git(fork, "rev-parse", "HEAD")).IsEqualTo(mine);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(origin);
    }

    // As Pull: part way through a merge of the user's own, its conflicts settled, the merge git
    // then refuses to begin would be undone, taking that one with it.
    [Test]
    public async Task SyncForkLeavesAMergeNotFinished()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        await File.WriteAllTextAsync(Path.Combine(fork, "tracked.txt"), "mine");
        Scratch.Git(fork, "commit", "-am", "Mine");
        code.CommitUpstream("Repo", "tracked.txt", "theirs");
        Scratch.Git(fork, "fetch", "upstream");
        var merge = CodeCommands.Git(fork, "merge", "upstream/main");
        await File.WriteAllTextAsync(Path.Combine(fork, "tracked.txt"), "settled");
        Scratch.Git(fork, "add", "tracked.txt");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(merge.Succeeded).IsFalse();
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("It is part way through a merge, so nothing was merged or pushed. Settle its conflicts in Changes and commit it, or abort it there.");
        await Assert.That(CodeCommands.Unfinished(fork)).IsEqualTo("merge");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(fork, "tracked.txt"))).IsEqualTo("settled");
    }

    // Git won't merge over an uncommitted change to a file the merge changes: nothing is
    // merged, and the change stays.
    [Test]
    public async Task SyncForkKeepsUncommittedChangesInTheWay()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        await File.WriteAllTextAsync(Path.Combine(fork, "tracked.txt"), "uncommitted");
        var before = Scratch.Git(fork, "rev-parse", "HEAD");
        code.CommitUpstream("Repo", "tracked.txt", "theirs");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("Couldn't merge upstream/main");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(fork, "tracked.txt"))).IsEqualTo("uncommitted");
        await Assert.That(Scratch.Git(fork, "rev-parse", "HEAD")).IsEqualTo(before);
    }

    // Origin has a commit the fork hasn't, from syncing on GitHub say: it comes in first, so the
    // push isn't refused.
    [Test]
    public async Task SyncForkCatchesUpWithOriginFirst()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        var other = code.Clone("Repo", "Other");
        await File.WriteAllTextAsync(Path.Combine(other, "other.txt"), "other");
        Scratch.Git(other, "add", "other.txt");
        Scratch.Git(other, "commit", "-m", "Other");
        Scratch.Git(other, "push", "origin");
        code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(File.Exists(Path.Combine(fork, "other.txt"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(fork, "upstream.txt"))).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(Scratch.Git(fork, "rev-parse", "HEAD"));
    }

    // Commits both here and on origin, that the other hasn't: there is no telling how that is to
    // be settled, so nothing is done.
    [Test]
    public async Task SyncForkLeavesABranchThatHasDivergedFromOrigin()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        var other = code.Clone("Repo", "Other");
        await File.WriteAllTextAsync(Path.Combine(other, "other.txt"), "other");
        Scratch.Git(other, "add", "other.txt");
        Scratch.Git(other, "commit", "-m", "Other");
        Scratch.Git(other, "push", "origin");
        await File.WriteAllTextAsync(Path.Combine(fork, "mine.txt"), "mine");
        Scratch.Git(fork, "add", "mine.txt");
        Scratch.Git(fork, "commit", "-m", "Mine");
        var mine = Scratch.Git(fork, "rev-parse", "HEAD");
        code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).Contains("diverged from origin/main: 1 commit here aren't there");
        await Assert.That(Scratch.Git(fork, "rev-parse", "HEAD")).IsEqualTo(mine);
    }

    // An upstream added by hand has no HEAD recorded: upstream is asked, and its answer
    // recorded, for the state to count against from then on.
    [Test]
    public async Task SyncForkRecordsUpstreamsDefaultBranch()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        Scratch.Git(fork, "remote", "set-head", "upstream", "--delete");
        var upstream = code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(fork, "symbolic-ref", "refs/remotes/upstream/HEAD")).IsEqualTo("refs/remotes/upstream/main");
        await Assert.That(Scratch.Git(fork, "rev-parse", "HEAD")).IsEqualTo(upstream);
    }

    // As Push, nothing to merge into with no branch checked out.
    [Test]
    public async Task SyncForkLeavesAForkWithNoBranchCheckedOut()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        Scratch.Git(fork, "switch", "--detach");
        code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).IsEqualTo("No branch is checked out in it, so nothing was merged or pushed.");
    }

    // A branch of the fork's own, made here, tracks none: upstream is merged into it, then it
    // goes to origin, under its own name, which it tracks from then on, rather than the push
    // being refused once the merge is made.
    [Test]
    public async Task SyncForkPublishesABranchTrackingNone()
    {
        using var code = new Scratch();
        var fork = code.CreateFork("Repo");
        Scratch.Git(fork, "config", "push.autoSetupRemote", "false");
        Scratch.Git(fork, "switch", "-c", "feature");
        var upstream = code.CommitUpstream("Repo", "upstream.txt", "upstream");

        var result = (await CodeCommands.SyncFork([fork])).Single();

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Output).EndsWith("Published feature to origin, as origin/feature, which it tracks from now on.");
        await Assert.That(Scratch.Git(fork, "rev-parse", "HEAD")).IsEqualTo(upstream);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "feature")).IsEqualTo(upstream);
        await Assert.That(Scratch.Git(fork, "rev-parse", "--abbrev-ref", "feature@{upstream}")).IsEqualTo("origin/feature");
    }

    // A tag goes on the commit each repository is at, and to origin. A second of the same name
    // fails, leaving the first.
    [Test]
    public async Task TagTagsAndPushes()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");

        var tagged = (await CodeCommands.Tag([repo], "v1.0")).Single();
        var again = (await CodeCommands.Tag([repo], "v1.0")).Single();

        await Assert.That(tagged.Succeeded).IsTrue();
        await Assert.That(again.Succeeded).IsFalse();
        await Assert.That(Scratch.Git(repo, "rev-parse", "v1.0^{commit}")).IsEqualTo(Scratch.Git(repo, "rev-parse", "HEAD"));
        await Assert.That(Scratch.Git(code.Origin("Repo"), "tag")).IsEqualTo("v1.0");
    }

    static GitCommit Commit(string repo, string sha)
    {
        var parents = Scratch.Git(repo, "rev-list", "--parents", "-n", "1", sha)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .ToList();
        return new(sha, parents, "", DateTimeOffset.Now, [], "");
    }

    // History's Revert commit: a new commit undoing the one picked, on the branch, not pushed.
    [Test]
    public async Task RevertCommitsWhatUndoesTheCommit()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Scratch.Git(repo, "commit", "-am", "Second");
        var second = Scratch.Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "other");
        Scratch.Git(repo, "add", "other.txt");
        Scratch.Git(repo, "commit", "-m", "Third");
        var third = Scratch.Git(repo, "rev-parse", "HEAD");

        var reverted = (await CodeCommands.Revert(repo, Commit(repo, second))).Single();

        await Assert.That(reverted.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD^")).IsEqualTo(third);
        await Assert.That(Scratch.Git(repo, "log", "-1", "--format=%s")).IsEqualTo("Revert \"Second\"");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "other.txt"))).IsTrue();
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEmpty();
    }

    // A merge is undone against its first parent, the branch it was merged into.
    [Test]
    public async Task RevertUndoesAMergeAgainstItsFirstParent()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var main = Scratch.Git(repo, "symbolic-ref", "--short", "HEAD");
        Scratch.Git(repo, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo, "feature.txt"), "feature");
        Scratch.Git(repo, "add", "feature.txt");
        Scratch.Git(repo, "commit", "-m", "Feature");
        Scratch.Git(repo, "checkout", main);
        await File.WriteAllTextAsync(Path.Combine(repo, "main.txt"), "main");
        Scratch.Git(repo, "add", "main.txt");
        Scratch.Git(repo, "commit", "-m", "Main");
        Scratch.Git(repo, "merge", "--no-ff", "--no-edit", "feature");
        var merge = Scratch.Git(repo, "rev-parse", "HEAD");

        var reverted = (await CodeCommands.Revert(repo, Commit(repo, merge))).Single();

        await Assert.That(reverted.Succeeded).IsTrue();
        await Assert.That(File.Exists(Path.Combine(repo, "feature.txt"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(repo, "main.txt"))).IsTrue();
    }

    // One that conflicts with what has changed since is given up: nothing committed, and the
    // repository not left part way through a revert.
    [Test]
    public async Task RevertThatConflictsIsGivenUp()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "second");
        Scratch.Git(repo, "commit", "-am", "Second");
        var second = Scratch.Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "third");
        Scratch.Git(repo, "commit", "-am", "Third");
        var third = Scratch.Git(repo, "rev-parse", "HEAD");

        var reverted = (await CodeCommands.Revert(repo, Commit(repo, second))).Single();

        await Assert.That(reverted.Succeeded).IsFalse();
        await Assert.That(reverted.Output).Contains("given up");
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(third);
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(repo, ".git", "REVERT_HEAD"))).IsFalse();
    }

    // History's Undo commit: the last commit off the branch, its changes left uncommitted and
    // unstaged, a file it added untracked again, and what was uncommitted already as it was.
    [Test]
    public async Task UndoCommitLeavesItsChangesUncommitted()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var initial = Scratch.Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "added.txt"), "added");
        Scratch.Git(repo, "add", ".");
        Scratch.Git(repo, "commit", "-m", "Second");
        var second = Scratch.Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "other");

        var undone = (await CodeCommands.UndoCommit(repo, Commit(repo, second))).Single();

        await Assert.That(undone.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(initial);
        await Assert.That(Scratch.Git(repo, "diff", "--name-only")).IsEqualTo("tracked.txt");
        await Assert.That(Scratch.Git(repo, "diff", "--cached", "--name-only")).IsEmpty();
        await Assert.That(Scratch.Git(repo, "ls-files", "--others", "--exclude-standard").Split('\n')).IsEquivalentTo(["added.txt", "other.txt"]);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("changed");
    }

    // The first commit undone leaves the branch with none, still checked out, its files staged.
    [Test]
    public async Task UndoCommitOfTheFirstCommit()
    {
        using var code = new Scratch();
        var repo = code.At("Fresh");
        Directory.CreateDirectory(repo);
        Scratch.Git(repo, "init", "--initial-branch=main");
        Scratch.Identify(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "first.txt"), "first");
        Scratch.Git(repo, "add", ".");
        Scratch.Git(repo, "commit", "-m", "First");
        var first = Scratch.Git(repo, "rev-parse", "HEAD");

        var undone = (await CodeCommands.UndoCommit(repo, Commit(repo, first))).Single();

        await Assert.That(undone.Succeeded).IsTrue();
        await Assert.That(CodeCommands.Git(repo, "rev-parse", "--verify", "--quiet", "HEAD").Succeeded).IsFalse();
        await Assert.That(Scratch.Git(repo, "symbolic-ref", "--short", "HEAD")).IsEqualTo("main");
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEqualTo("A  first.txt");
    }

    // Nothing is undone where the commit has been pushed, or isn't the last any more, as
    // History's arrows may be older than the branch.
    [Test]
    public async Task UndoCommitLeavesAPushedOrOlderCommit()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var pushed = Scratch.Git(repo, "rev-parse", "HEAD");
        var pushedUndone = (await CodeCommands.UndoCommit(repo, Commit(repo, pushed))).Single();
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "second");
        Scratch.Git(repo, "commit", "-am", "Second");
        var second = Scratch.Git(repo, "rev-parse", "HEAD");
        Scratch.Git(repo, "commit", "--allow-empty", "-m", "Third");
        var third = Scratch.Git(repo, "rev-parse", "HEAD");

        var olderUndone = (await CodeCommands.UndoCommit(repo, Commit(repo, second))).Single();

        await Assert.That(pushedUndone.Succeeded).IsFalse();
        await Assert.That(pushedUndone.Output).IsEqualTo($"Nothing was undone: {pushed[..7]} has been pushed, and changing it would mean pushing with force.");
        await Assert.That(olderUndone.Succeeded).IsFalse();
        await Assert.That(olderUndone.Output).IsEqualTo($"Nothing was undone: {second[..7]} isn't the last commit on the branch any more.");
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(third);
        await Assert.That(Scratch.Git(repo, "status", "--porcelain")).IsEmpty();
    }

    // On a branch tracking none, as GitHub Desktop has it: a commit no remote's branch has is
    // undone, and one a remote's branch has isn't.
    [Test]
    public async Task UndoCommitOnABranchTrackingNone()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var initial = Scratch.Git(repo, "rev-parse", "HEAD");
        Scratch.Git(repo, "switch", "-q", "--no-track", "-c", "feature", "origin/main");
        var atOrigin = (await CodeCommands.UndoCommit(repo, Commit(repo, initial))).Single();
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "feature");
        Scratch.Git(repo, "commit", "-am", "Feature");
        var feature = Scratch.Git(repo, "rev-parse", "HEAD");

        var undone = (await CodeCommands.UndoCommit(repo, Commit(repo, feature))).Single();

        await Assert.That(atOrigin.Succeeded).IsFalse();
        await Assert.That(atOrigin.Output).IsEqualTo($"Nothing was undone: {initial[..7]} is on a remote's branch already.");
        await Assert.That(undone.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(repo, "rev-parse", "HEAD")).IsEqualTo(initial);
        await Assert.That(Scratch.Git(repo, "diff", "--name-only")).IsEqualTo("tracked.txt");
    }

    // What Undo commit asks: what becomes of the commit, of what is typed and uncommitted
    // already, and of a merge's commits or the branch of a first commit.
    [Test]
    public async Task UndoCommitQuestion()
    {
        var commit = new GitCommit("e5f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9", ["a1b2c3d4"], "Ada", DateTimeOffset.Now, [], "Rename several items");
        var merge = commit with
        {
            Parents = ["a1b2c3d4", "b2c3d4e5"]
        };
        var first = commit with
        {
            Parents = []
        };

        await Assert.That(CodeMenu.UndoCommitQuestion(@"C:\Code\Lode", commit, false, false))
            .IsEqualTo("Undo e5f1a2b \"Rename several items\" in Lode?\n\nIt comes off the branch, its changes left uncommitted, and its summary and description go in Changes, to commit again.");
        await Assert.That(CodeMenu.UndoCommitQuestion(@"C:\Code\Lode", commit, true, true))
            .IsEqualTo("Undo e5f1a2b \"Rename several items\" in Lode?\n\nIt comes off the branch, its changes left uncommitted, and its summary and description go in Changes, to commit again. They replace what is typed there.\n\nWhat is uncommitted already stays as it is, among its changes.");
        await Assert.That(CodeMenu.UndoCommitQuestion(@"C:\Code\Lode", merge, false, false))
            .EndsWith("\n\nIt is a merge: what it brought in is left uncommitted, and the commits it merged are no longer on the branch.");
        await Assert.That(CodeMenu.UndoCommitQuestion(@"C:\Code\Lode", first, false, false))
            .EndsWith("\n\nIt is the first commit: the branch is left with none, its files staged.");
    }

    // Given a commit, as History tags the one picked, the tag goes on that, not the one checked
    // out, and to origin.
    [Test]
    public async Task TagTagsTheCommitGiven()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        var first = Scratch.Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Scratch.Git(repo, "commit", "-am", "Second");

        var tagged = (await CodeCommands.Tag([repo], "v0.9", commit: first)).Single();

        await Assert.That(tagged.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(repo, "rev-parse", "v0.9^{commit}")).IsEqualTo(first);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "v0.9^{commit}")).IsEqualTo(first);
        // On origin's branch already, the tag goes alone: the commit after it stays unpushed.
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(first);
    }

    // Given a commit not yet pushed, as History tags one, it goes with the tag, with those
    // before it, but not those after it, so origin's branch has the commit tagged, not only the
    // tag, and the branch is no longer ahead by it.
    [Test]
    public async Task TagPushesTheCommitGivenWhereNotYetPushed()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        foreach (var name in new[] { "Second", "Third", "Fourth" })
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), name);
            Scratch.Git(repo, "commit", "-am", name);
        }

        var third = Scratch.Git(repo, "rev-parse", "HEAD~1");
        var head = Scratch.Git(repo, "rev-parse", "HEAD");

        var older = (await CodeCommands.Tag([repo], "v0.9", commit: third)).Single();

        await Assert.That(older.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(third);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "v0.9^{commit}")).IsEqualTo(third);
        await Assert.That(Scratch.Git(repo, "rev-list", "--count", "@{upstream}..HEAD")).IsEqualTo("1");

        var checkedOut = (await CodeCommands.Tag([repo], "v1.0", commit: head)).Single();

        await Assert.That(checkedOut.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(head);
        await Assert.That(Scratch.Git(repo, "rev-list", "--count", "@{upstream}..HEAD")).IsEqualTo("0");
    }

    // Given a commit on a branch origin hasn't got, the branch goes up to it with the tag, and
    // tracks origin's from then on, though git tracks only a branch pushed by name. Given another
    // branch's, as History shows while comparing, the tag goes alone.
    [Test]
    public async Task TagOfACommitGivenPushesANewBranchUpToIt()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "other");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "other");
        Scratch.Git(repo, "commit", "-am", "Other");
        var other = Scratch.Git(repo, "rev-parse", "HEAD");
        Scratch.Git(repo, "switch", "main");
        Scratch.Git(repo, "switch", "-c", "feature");
        foreach (var name in new[] { "First", "Second" })
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), name);
            Scratch.Git(repo, "commit", "-am", name);
        }

        var first = Scratch.Git(repo, "rev-parse", "HEAD~1");

        var branch = (await CodeCommands.Tag([repo], "v1.0", commit: first)).Single();
        var otherBranch = (await CodeCommands.Tag([repo], "v0.1", commit: other)).Single();

        await Assert.That((branch.Succeeded, otherBranch.Succeeded)).IsEqualTo((true, true));
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "feature")).IsEqualTo(first);
        await Assert.That(Scratch.Git(repo, "rev-parse", "--abbrev-ref", "@{upstream}")).IsEqualTo("origin/feature");
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "v0.1^{commit}")).IsEqualTo(other);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "branch", "--list", "other")).IsEqualTo("");
    }

    // Commits not yet pushed go with the tag, so origin's branch has the commit tagged, not only
    // the tag.
    [Test]
    public async Task TagPushesCommitsNotYetPushedWithIt()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Scratch.Git(repo, "commit", "-am", "Second");

        var tagged = (await CodeCommands.Tag([repo], "v1.0")).Single();

        var head = Scratch.Git(repo, "rev-parse", "HEAD");
        await Assert.That(tagged.Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "main")).IsEqualTo(head);
        await Assert.That(Scratch.Git(code.Origin("Repo"), "rev-parse", "v1.0^{commit}")).IsEqualTo(head);
    }

    // Given a commit message, the changes are committed first, new files too, and are in the
    // tag. Without one, the tag is of what is committed, and the changes stay as they are.
    [Test]
    public async Task TagCommitsTheChangesOnlyWhenAsked()
    {
        using var code = new Scratch();
        var asked = code.CreateRepo("Asked");
        var notAsked = code.CreateRepo("NotAsked");
        foreach (var repo in new[] { asked, notAsked })
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
            await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "new");
        }

        var before = Scratch.Git(notAsked, "rev-parse", "HEAD");

        var committed = (await CodeCommands.Tag([asked], "v1.0", commitMessage: "Release")).Single();
        var left = (await CodeCommands.Tag([notAsked], "v1.0")).Single();

        await Assert.That((committed.Succeeded, left.Succeeded)).IsEqualTo((true, true));
        await Assert.That(Scratch.Git(asked, "log", "-1", "--format=%s", "v1.0")).IsEqualTo("Release");
        await Assert.That(Scratch.Git(asked, "status", "--porcelain")).IsEqualTo("");
        await Assert.That(Scratch.Git(code.Origin("Asked"), "rev-parse", "main")).IsEqualTo(Scratch.Git(asked, "rev-parse", "HEAD"));
        await Assert.That(Scratch.Git(notAsked, "rev-parse", "v1.0^{commit}")).IsEqualTo(before);
        await Assert.That(Scratch.Git(notAsked, "status", "--porcelain")).IsNotEqualTo("");
    }

    // A branch diverged from origin's, as last fetched, is left untagged, to be pulled first.
    // One not known to have diverged, origin's not fetched since, has its push refused, and the
    // tag is taken off again, so it can be tagged once pulled.
    [Test]
    public async Task TagLeavesABranchThatHasDiverged()
    {
        using var code = new Scratch();
        var fetched = code.CreateRepo("Fetched");
        var unfetched = code.CreateRepo("Unfetched");
        foreach (var name in new[] { "Fetched", "Unfetched" })
        {
            var other = code.Clone(name, $"{name}Other");
            await File.WriteAllTextAsync(Path.Combine(other, "tracked.txt"), "from elsewhere");
            Scratch.Git(other, "commit", "-am", "Elsewhere");
            Scratch.Git(other, "push", "origin");
            var repo = code.At(name);
            await File.WriteAllTextAsync(Path.Combine(repo, "local.txt"), "local");
            Scratch.Git(repo, "add", ".");
            Scratch.Git(repo, "commit", "-m", "Local");
        }

        Scratch.Git(fetched, "fetch");

        var known = (await CodeCommands.Tag([fetched], "v1.0")).Single();
        var refused = (await CodeCommands.Tag([unfetched], "v1.0")).Single();

        await Assert.That((known.Succeeded, refused.Succeeded)).IsEqualTo((false, false));
        await Assert.That(known.Output).Contains("Pull first");
        await Assert.That(refused.Output).Contains("The tag was taken off again");
        await Assert.That(Scratch.Git(fetched, "tag")).IsEqualTo("");
        await Assert.That(Scratch.Git(unfetched, "tag")).IsEqualTo("");
        await Assert.That(Scratch.Git(code.Origin("Fetched"), "tag")).IsEqualTo("");
        await Assert.That(Scratch.Git(code.Origin("Unfetched"), "tag")).IsEqualTo("");
    }

    // A branch origin hasn't got goes with the tag, and tracks origin's from then on. Detached,
    // the tag goes alone.
    [Test]
    public async Task TagPushesANewBranchButNotADetachedHead()
    {
        using var code = new Scratch();
        var branched = code.CreateRepo("Branched");
        var detached = code.CreateRepo("Detached");
        Scratch.Git(branched, "switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(branched, "tracked.txt"), "feature");
        Scratch.Git(branched, "commit", "-am", "Feature");
        Scratch.Git(detached, "switch", "--detach");
        await File.WriteAllTextAsync(Path.Combine(detached, "tracked.txt"), "detached");
        Scratch.Git(detached, "commit", "-am", "Detached");

        var results = await CodeCommands.Tag([branched, detached], "v1.0");

        await Assert.That(results.All(_ => _.Succeeded)).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Branched"), "rev-parse", "feature")).IsEqualTo(Scratch.Git(branched, "rev-parse", "HEAD"));
        await Assert.That(Scratch.Git(branched, "rev-parse", "--abbrev-ref", "@{upstream}")).IsEqualTo("origin/feature");
        await Assert.That(Scratch.Git(code.Origin("Detached"), "rev-parse", "v1.0^{commit}")).IsEqualTo(Scratch.Git(detached, "rev-parse", "HEAD"));
        await Assert.That(Scratch.Git(code.Origin("Detached"), "branch", "--format=%(refname:short)")).IsEqualTo("main");
    }

    // The tag offered is the one after the latest, where that is numbers and dots, and the same
    // for every repository.
    [Test]
    public async Task TheTagOffered()
    {
        static CodeMenu.TagRead Read(string? tag) =>
            new("Repo", new("main", "abc1234", 0, 0, []) { Tag = tag }, []);

        await Assert.That(CodeMenu.NextTag("2.0.0")).IsEqualTo("2.0.1");
        await Assert.That(CodeMenu.NextTag("v1.9")).IsEqualTo("v1.10");
        await Assert.That(CodeMenu.NextTag("7")).IsEqualTo("8");
        await Assert.That(CodeMenu.NextTag("133.0.88364-dev")).IsNull();
        await Assert.That(CodeMenu.NextTag("release")).IsNull();
        await Assert.That(CodeMenu.NextTag(null)).IsNull();
        await Assert.That(CodeMenu.SuggestTag([Read("2.0.0"), Read("2.0.0")])).IsEqualTo("2.0.1");
        await Assert.That(CodeMenu.SuggestTag([Read("2.0.0"), Read("1.0.0")])).IsEqualTo("");
        await Assert.That(CodeMenu.SuggestTag([Read(null)])).IsEqualTo("");
    }

    // Tag reads the commit checked out, to say it by its short SHA and subject, and its tags.
    [Test]
    public async Task TagReadsTheCommitCheckedOut()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "tag", "1.0");

        var read = CodeMenu.ReadForTag(repo);

        await Assert.That(read.Commit).IsEqualTo($"{Scratch.Git(repo, "rev-parse", "--short=7", "HEAD")} Initial");
        await Assert.That(read.AtHead).IsEquivalentTo(["1.0"]);
    }

    // What Tag asks says what it tags, by short SHA and subject, what goes with the tag, and what
    // doesn't.
    [Test]
    public async Task WhatTagAsks()
    {
        var clean = new GitState("main", "abc1234", 0, 0, [])
        {
            Upstream = true
        };
        var changed = clean with
        {
            Files = [new("tracked.txt", GitChange.Modified), new("new.txt", GitChange.Untracked)]
        };

        await Verify(
            new
            {
                clean = CodeMenu.TagQuestion([new(@"C:\Code\Lode", clean, [], "Release 2.0.1")]),
                ahead = CodeMenu.TagQuestion([new(@"C:\Code\Lode", clean with { Ahead = 1 }, ["2.0.0"])]),
                diverged = CodeMenu.TagQuestion([new(@"C:\Code\Lode", clean with { Ahead = 2, Behind = 1 }, [])]),
                newBranch = CodeMenu.TagQuestion([new(@"C:\Code\Lode", clean with { Branch = "feature", Upstream = false }, [])]),
                detached = CodeMenu.TagQuestion([new(@"C:\Code\Lode", clean with { Branch = null, Upstream = false }, [])]),
                changed = CodeMenu.TagQuestion([new(@"C:\Code\Lode", changed, [], "Fix the build")]),
                several = CodeMenu.TagQuestion(
                [
                    new(@"C:\Code\Argon", changed with { Ahead = 3 }, []),
                    new(@"C:\Code\Lode", clean with { Ahead = 1, Behind = 1 }, []),
                    new(@"C:\Code\Morph", changed, ["1.0"]),
                    new(@"C:\Code\Unread", null, [])
                ])
            })
            .Snapshot(
                """
                {
                  clean:
                Tag the commit Lode has checked out, and push the tag to origin:

                abc1234 Release 2.0.1,
                  ahead:
                Tag the commit Lode has checked out, and push the tag to origin:

                abc1234

                Its 1 commit not yet pushed goes with the tag, so origin's main has the commit tagged.

                The commit is tagged 2.0.0 already.,
                  diverged:
                Tag the commit Lode has checked out, and push the tag to origin:

                abc1234

                main and origin's have each got commits the other hasn't, so it can't be tagged till pulled.,
                  newBranch:
                Tag the commit Lode has checked out, and push the tag to origin:

                abc1234

                feature goes to origin with the tag, as origin hasn't got it yet.,
                  detached:
                Tag the commit Lode has checked out, and push the tag to origin:

                abc1234

                No branch is checked out, so the tag goes alone.,
                  changed:
                Tag the commit Lode has checked out, and push the tag to origin:

                abc1234 Fix the build

                Its 2 uncommitted changes won't be in the tag unless committed first, when the tag goes on the new commit instead.,
                  several:
                Tag the commit each of 4 repositories has checked out, and push the tag to origin:

                Argon: abc1234
                Lode: abc1234
                Morph: abc1234
                Unread

                Lode has diverged from origin, so won't be tagged till pulled.

                Argon has commits not yet pushed, which go with the tag.

                Morph is tagged already.

                Argon and Morph have uncommitted changes, which won't be in the tag unless committed first.
                }
                """);
    }

    // Deleting a tag takes it from the repository and from origin. One never pushed goes from
    // the repository alone; one in neither fails.
    [Test]
    public async Task DeleteTagDeletesItEverywhere()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await CodeCommands.Tag([repo], "v1.0");
        Scratch.Git(repo, "tag", "local-only");

        var pushed = (await CodeCommands.DeleteTag([repo], "v1.0")).Single();
        var localOnly = (await CodeCommands.DeleteTag([repo], "local-only")).Single();
        var missing = (await CodeCommands.DeleteTag([repo], "missing")).Single();

        await Assert.That((pushed.Succeeded, localOnly.Succeeded, missing.Succeeded)).IsEqualTo((true, true, false));
        await Assert.That(Scratch.Git(repo, "tag")).IsEqualTo("");
        await Assert.That(Scratch.Git(code.Origin("Repo"), "tag")).IsEqualTo("");
    }

    // Git refuses an empty commit message, and a tag with a space in it.
    [Test]
    public async Task WhatTheGitDialogTakes()
    {
        await Assert.That(CodeMenu.GitTextForm.IsValid("refs or cleanup", isTagName: false)).IsTrue();
        await Assert.That(CodeMenu.GitTextForm.IsValid("", isTagName: false)).IsFalse();
        await Assert.That(CodeMenu.GitTextForm.IsValid("v1.0", isTagName: true)).IsTrue();
        await Assert.That(CodeMenu.GitTextForm.IsValid("v 1", isTagName: true)).IsFalse();
    }

    // A clone records origin's default branch, which need not be called main, and switching
    // makes a local branch of it where there is none yet.
    [Test]
    public async Task SwitchGoesToTheDefaultBranchACloneRecorded()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "trunk");
        Scratch.Git(repo, "push", "origin", "trunk");
        Scratch.Git(code.Origin("Repo"), "symbolic-ref", "HEAD", "refs/heads/trunk");
        var clone = code.Clone("Repo", "Clone");
        Scratch.Git(clone, "switch", "-c", "feature");
        Scratch.Git(clone, "branch", "-D", "trunk");

        var results = await CodeCommands.SwitchToDefaultBranch([clone]);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(clone, "branch", "--show-current")).IsEqualTo("trunk");
        await Assert.That(Scratch.Git(clone, "rev-parse", "--abbrev-ref", "trunk@{upstream}")).IsEqualTo("origin/trunk");
    }

    // A repository made by init, then given an origin, has no default branch recorded, so
    // origin is asked.
    [Test]
    public async Task SwitchAsksOriginWhenNoDefaultBranchIsRecorded()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "switch", "-c", "feature");

        var results = await CodeCommands.SwitchToDefaultBranch([repo]);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(repo, "branch", "--show-current")).IsEqualTo("main");
    }

    // Anything uncommitted, staged or not, or a file git doesn't track, fails the repository,
    // which stays on its branch with its changes, while the others are still switched.
    [Test]
    public async Task SwitchFailsForARepoWithUncommittedChanges()
    {
        using var code = new Scratch();
        var clean = code.CreateRepo("Clean");
        var changed = code.CreateRepo("Changed");
        var staged = code.CreateRepo("Staged");
        var untracked = code.CreateRepo("Untracked");
        foreach (var repo in new[] {clean, changed, staged, untracked})
        {
            Scratch.Git(repo, "switch", "-c", "feature");
        }

        await File.WriteAllTextAsync(Path.Combine(changed, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(staged, "added.txt"), "added");
        Scratch.Git(staged, "add", "added.txt");
        await File.WriteAllTextAsync(Path.Combine(untracked, "new.txt"), "new");

        var results = await CodeCommands.SwitchToDefaultBranch([changed, clean, staged, untracked]);

        await Assert.That(results.Select(_ => (code.Relative(_.Repo), _.Succeeded, Branch: Scratch.Git(_.Repo, "branch", "--show-current"))))
            .IsEquivalentTo(
            [
                ("Changed", false, "feature"),
                ("Clean", true, "main"),
                ("Staged", false, "feature"),
                ("Untracked", false, "feature")
            ]);
        await Assert.That(results[0].Output).IsEqualTo("Uncommitted changes, so not switched:\nM tracked.txt");
        await Assert.That(results[2].Output).IsEqualTo("Uncommitted changes, so not switched:\nA  added.txt");
        await Assert.That(results[3].Output).IsEqualTo("Uncommitted changes, so not switched:\n?? new.txt");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(changed, "tracked.txt"))).IsEqualTo("changed");
    }

    // A repository with no origin has no default branch to go to.
    [Test]
    public async Task SwitchFailsWithoutAnOrigin()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        Scratch.Git(repo, "remote", "remove", "origin");

        var result = (await CodeCommands.SwitchToDefaultBranch([repo])).Single();

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Output).StartsWith("Couldn't ask origin for its default branch:\n");
    }

    [Test]
    public async Task ReportsEachRepoWhenThereAreSeveral()
    {
        using var code = new Scratch();
        var one = code.CreateRepo("One");
        var two = code.CreateRepo("Two");
        var several = new Recorder();
        var single = new Recorder();

        await CodeCommands.Pull([one, two], several);
        await CodeCommands.Pull([one], single);

        await Assert.That(several.Reports).IsEquivalentTo(["1 of 2 done", "2 of 2 done"]);
        await Assert.That(single.Reports).IsEmpty();
    }

    // Each repository is its own working tree, so git runs in several at once: a code folder's
    // pulls are not one wait on the network after another. What each gave is in the order the
    // repositories were given, whichever finished first.
    [Test]
    public async Task RunsInSeveralAtOnceKeepingTheirOrder()
    {
        var items = Enumerable.Range(0, 12).Select(_ => $"Repo{_}").ToList();
        var running = 0;
        var most = 0;

        var results = CodeCommands.InParallel(
            items,
            item =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                // The later ones quicker, so they finish first.
                Thread.Sleep(400 - items.IndexOf(item) * 20);
                Interlocked.Decrement(ref running);
                return item;
            });

        await Assert.That(results).IsEquivalentTo(items, CollectionOrdering.Matching);
        await Assert.That(most).IsGreaterThan(1);
        await Assert.That(most).IsLessThanOrEqualTo(CodeCommands.AtOnce);
    }

    static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    [Test]
    public Task NugetProgress() =>
        Verify(
            new
            {
                startingTheOnlySolution = CodeCommands.NugetProgress(new(@"C:\Code\Lode\src\Lode.slnx", 1, 1, null, 0, 0)),
                checkingAPackage = CodeCommands.NugetProgress(new(@"C:\Code\Lode\src\Lode.slnx", 1, 1, "Serilog", 4, 21)),
                oneOfSeveral = CodeCommands.NugetProgress(new(@"C:\Code\Lode\.config\dotnet-tools.json", 2, 3, "dotnet-ef", 1, 1))
            })
            .Snapshot(
                """
                {
                  startingTheOnlySolution: Lode.slnx,
                  checkingAPackage: Lode.slnx: Serilog, 4 of 21,
                  oneOfSeveral: dotnet-tools.json (2 of 3): dotnet-ef, 1 of 1
                }
                """);

    // Git reports from a worker thread.
    sealed class Recorder : IProgress<string>
    {
        List<string> reports = [];

        public List<string> Reports
        {
            get
            {
                lock (reports)
                {
                    return [..reports];
                }
            }
        }

        public void Report(string value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    [Test]
    public Task OutcomePutsFailuresFirst() =>
        Verify(
            CodeCommands.GitOutcome(
                [
                    new(@"C:\Code\Good", true, "Already up to date."),
                    new(@"C:\Code\Bad", false, "\nfatal: unable to access 'https://example.com/': Could not resolve host\nmore")
                ]))
                .Snapshot(
                    """
                    {
                      Succeeded: false,
                      Summary: 1 of 2 failed,
                      Details:
                    Bad: FAILED
                      fatal: unable to access 'https://example.com/': Could not resolve host
                      more

                    Good: done
                      Already up to date.
                    }
                    """);

    [Test]
    public Task OutcomeWhenAllSucceed() =>
        Verify(
            CodeCommands.GitOutcome(
                [
                    new(@"C:\Code\One", true, "Already up to date."),
                    new(@"C:\Code\Two", true, "Fast-forward")
                ]))
                .Snapshot(
                    """
                    {
                      Succeeded: true,
                      Summary: 2 repositories done,
                      Details:
                    One: done
                      Already up to date.

                    Two: done
                      Fast-forward
                    }
                    """);

    [Test]
    public Task OutcomeWithNoRepos() =>
        Verify(CodeCommands.GitOutcome([]))
            .Snapshot(
                """
                {
                  Succeeded: true,
                  Summary: No git repositories,
                  Details: 
                }
                """);

    // Each package changed is listed under its file, then the problems. A solution that could
    // not be updated makes it a failure.
    [Test]
    public Task NugetOutcomeListsEachPackageByFile() =>
        Verify(
            CodeCommands.NugetOutcome(
                @"C:\Code\CountryData",
                [
                    Change(PackageChangeKind.Version, @"src\Directory.Packages.props", "MarkdownSnippets.MsBuild", "28.5.1", "29.0.0"),
                    Change(PackageChangeKind.Version, @"src\Directory.Packages.props", "xunit.v3", "4.0.2", "4.0.3"),
                    Change(PackageChangeKind.Migration, @"src\Directory.Packages.props", "Microsoft.Azure.Storage.Blob", "11.2.3", "12.24.0") with
                    {
                        Alternative = "Azure.Storage.Blobs",
                        Projects = [@"C:\Code\CountryData\src\CountryData\CountryData.csproj"]
                    },
                    Change(PackageChangeKind.VersionOverride, @"src\Tests\Tests.csproj", "NUnit", "3.13.0", "4.3.2"),
                    Change(PackageChangeKind.Tool, @".config\dotnet-tools.json", "dotnet-ef", "9.0.0", "10.0.0")
                ],
                [
                    new(UpdateProblemKind.AlternativeNotFound, @"C:\Code\CountryData\src\Directory.Packages.props", "Package Old.Thing is deprecated with alternative New.Thing, but alternative not found in sources"),
                    new(UpdateProblemKind.NotCentralPackages, @"C:\Code\CountryData\Old\Old.sln", "Only central packages supported. Skipped")
                ]))
                .Snapshot(
                    """
                    {
                      Succeeded: false,
                      Summary: 5 packages updated, 2 problems,
                      Details:
                    src\Directory.Packages.props
                      MarkdownSnippets.MsBuild: 28.5.1 -> 29.0.0
                      xunit.v3: 4.0.2 -> 4.0.3
                      Microsoft.Azure.Storage.Blob 11.2.3 -> Azure.Storage.Blobs 12.24.0
                    src\Tests\Tests.csproj
                      NUnit VersionOverride: 3.13.0 -> 4.3.2
                    .config\dotnet-tools.json
                      dotnet-ef: 9.0.0 -> 10.0.0

                    src\Directory.Packages.props: Package Old.Thing is deprecated with alternative New.Thing, but alternative not found in sources
                    Old\Old.sln: Only central packages supported. Skipped
                    }
                    """);

    // A warning is listed, but everything that could be updated was.
    [Test]
    public async Task NugetOutcomeWithOnlyAWarningSucceeds()
    {
        var outcome = CodeCommands.NugetOutcome(
            @"C:\Code\CountryData",
            [],
            [new(UpdateProblemKind.ManifestUnreadable, @"C:\Code\CountryData\.config\dotnet-tools.json", "Failed to parse tool manifest. Error: bad json")]);

        await Assert.That(outcome).IsEqualTo(
            new(
                true,
                "Nothing to update, 1 problem",
                @".config\dotnet-tools.json: Failed to parse tool manifest. Error: bad json"));
    }

    [Test]
    public Task NugetOutcomeWithNothingToDo() =>
        Verify(CodeCommands.NugetOutcome(@"C:\Code\CountryData", [], []))
            .Snapshot(
                """
                {
                  Succeeded: true,
                  Summary: Nothing to update,
                  Details: 
                }
                """);

    static PackageChange Change(PackageChangeKind kind, string file, string package, string from, string to) =>
        new(kind, Path.Combine(@"C:\Code\CountryData", file), package, NuGetVersion.Parse(from), NuGetVersion.Parse(to));

    [Test]
    public Task DescribeNamesTheRepos() =>
        Verify(
            new
            {
                one = CodeMenu.Describe([@"C:\Code\Lode"]),
                few = CodeMenu.Describe([@"C:\Code\A", @"C:\Code\B"]),
                many = CodeMenu.Describe(Enumerable.Range(1, 10).Select(_ => $@"C:\Code\R{_}").ToList())
            })
            .Snapshot(
                """
                {
                  one: Lode,
                  few: 2 repositories (A, B),
                  many: 10 repositories (R1, R2, R3, R4, R5, R6, R7, R8, and 2 more)
                }
                """);

    // No solutions, so nothing to fetch: only checks the plumbing around PackageUpdateLib.
    [Test]
    public async Task UpdateNugetsInAnEmptyFolder()
    {
        using var code = new Scratch();

        var outcome = await CodeCommands.UpdateNugets(code.Root);

        await Assert.That(outcome).IsEqualTo(new(true, "Nothing to update", ""));
    }

    // One folder that can't be updated is a failure, but the others are still updated, and
    // each is reported as it is reached.
    [Test]
    public async Task UpdateNugetsInSeveralFolders()
    {
        using var code = new Scratch();
        Directory.CreateDirectory(code.At("One"));
        Directory.CreateDirectory(code.At("Two"));

        var fine = await CodeCommands.UpdateNugets([code.At("One"), code.At("Two")]);
        var oneMissing = await CodeCommands.UpdateNugets([code.At("Missing"), code.At("One")]);

        await Assert.That(fine).IsEqualTo(new(true, "Nothing to update", ""));
        await Assert.That(oneMissing.Succeeded).IsFalse();
        await Assert.That(oneMissing.Summary).IsEqualTo("Nothing to update, 1 problem");
        await Assert.That(oneMissing.Details).StartsWith("Missing: ");
    }

    public sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LodeTests", $"Code.{Guid.NewGuid():N}");

        public Scratch() =>
            Directory.CreateDirectory(Root);

        public string At(params string[] parts) =>
            Path.Combine([Root, .. parts]);

        public string Relative(string path) =>
            Path.GetRelativePath(Root, path).Replace('\\', '/');

        public string Origin(string name) =>
            At("origins", $"{name}.git");

        // A repository and a fork, as CreateRepo and CreateFork make them, made with git once a
        // run and copied from then on. A copy is the same, without the processes, nine for a
        // repository and seventeen for a fork, each taking a second or more while every test
        // around is running git too.
        static readonly Lazy<Scratch> repoTemplate = new(() =>
        {
            var template = new Scratch();
            template.MakeRepo("Repo");
            return template;
        });

        static readonly Lazy<Scratch> forkTemplate = new(() =>
        {
            var template = new Scratch();
            template.MakeFork("Repo");
            return template;
        });

        [After(TestSession)]
        public static void DeleteTemplates()
        {
            if (repoTemplate.IsValueCreated)
            {
                repoTemplate.Value.Dispose();
            }

            if (forkTemplate.IsValueCreated)
            {
                forkTemplate.Value.Dispose();
            }
        }

        // A repository with one committed file, tracking a bare origin it has pushed to.
        public string CreateRepo(string name)
        {
            var template = repoTemplate.Value;
            CopyFrom(
                (template.At("Repo"), At(name)),
                (template.Origin("Repo"), Origin(name)));
            return At(name);
        }

        // A fork, as GitHub Desktop clones one: the repository it was made from, NameUpstream,
        // its bare one standing in for GitHub's, a bare origin cloned from that, the fork on
        // GitHub, and a clone of origin, with upstream as a remote and its HEAD recorded.
        // NameUpstream is a repository too, to commit to upstream from.
        public string CreateFork(string name)
        {
            var template = forkTemplate.Value;
            CopyFrom(
                (template.At("RepoUpstream"), At($"{name}Upstream")),
                (template.Origin("RepoUpstream"), Origin($"{name}Upstream")),
                (template.Origin("Repo"), Origin(name)),
                (template.At("Repo"), At(name)));
            return At(name);
        }

        // Copies each folder, and points the remotes in the copies' configs at the copies.
        static void CopyFrom(params (string From, string To)[] folders)
        {
            foreach (var (from, to) in folders)
            {
                Copy(from, to);
            }

            // Longest first, so a folder named for another, RepoUpstream for Repo, isn't taken
            // for it.
            var paths = folders
                .OrderByDescending(_ => _.From.Length)
                .ToList();
            foreach (var (_, to) in folders)
            {
                var config = Path.Combine(to, ".git", "config");
                if (!File.Exists(config))
                {
                    config = Path.Combine(to, "config");
                }

                var text = File.ReadAllText(config);
                foreach (var (from, copy) in paths)
                {
                    // As git writes a path in its config, each backslash doubled.
                    text = text.Replace(from.Replace(@"\", @"\\"), copy.Replace(@"\", @"\\"));
                }

                File.WriteAllText(config, text);
            }
        }

        // Attributes and all: git marks .git hidden, and its object files read-only.
        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(from))
            {
                Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
            }

            // Last, once what is in it is: Windows updates the entry of a folder written to in
            // the folder above only when the folder is next opened, as this does, and otherwise
            // when the test looks in it, which a watch on the code folder hears as a change. Git
            // making a repository opens its folders as it goes.
            File.SetAttributes(to, File.GetAttributes(from));
        }

        void MakeRepo(string name)
        {
            var origin = Origin(name);
            Directory.CreateDirectory(origin);
            Git(origin, "init", "--bare", "--initial-branch=main");

            var repo = At(name);
            Directory.CreateDirectory(repo);
            Git(repo, "init", "--initial-branch=main");
            Identify(repo);
            File.WriteAllText(Path.Combine(repo, "tracked.txt"), "original");
            Git(repo, "add", ".");
            Git(repo, "commit", "-m", "Initial");
            Git(repo, "remote", "add", "origin", origin);
            Git(repo, "push", "-u", "origin", "main");
        }

        void MakeFork(string name)
        {
            CreateRepo($"{name}Upstream");
            Directory.CreateDirectory(At("origins"));
            Git(Root, "clone", "--bare", Origin($"{name}Upstream"), Origin(name));
            var fork = Clone(name, name);
            Git(fork, "remote", "add", "upstream", Origin($"{name}Upstream"));
            Git(fork, "fetch", "upstream");
            Git(fork, "remote", "set-head", "upstream", "main");
        }

        // Commits a file to the repository a fork was made from, as its owner would.
        public string CommitUpstream(string name, string file, string text)
        {
            var upstream = At($"{name}Upstream");
            File.WriteAllText(Path.Combine(upstream, file), text);
            Git(upstream, "add", file);
            Git(upstream, "commit", "-m", $"Upstream {file}");
            Git(upstream, "push", "origin", "main");
            return Git(upstream, "rev-parse", "HEAD");
        }

        public string Clone(string name, string cloneName)
        {
            var clone = At(cloneName);
            Git(Root, "clone", Origin(name), clone);
            Identify(clone);
            return clone;
        }

        // Who commits, in the repository itself, as CI's git has nobody set, and can't make
        // anybody up from a machine with no domain.
        public static void Identify(string repo)
        {
            Git(repo, "config", "user.name", "Test");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "commit.gpgsign", "false");
        }

        public static string Git(string directory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new($"git {string.Join(' ', arguments)} failed: {error.Result}");
            }

            return output.Trim();
        }

        public void Dispose()
        {
            try
            {
                // git marks its object files read-only.
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // Something still has a handle open; the temp folder is cleared eventually.
            }
        }
    }
}
