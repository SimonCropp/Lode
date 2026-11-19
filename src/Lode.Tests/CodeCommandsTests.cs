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

        await Assert.That(repos.Select(code.Relative)).IsEquivalentTo(["Group/Three", "Group/Two", "One"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task OutermostLeavesOutFoldersInsideOthers()
    {
        var folders = CodeCommands.Outermost([@"C:\Code\Lode", @"C:\Code\Lode\src", @"C:\Code\Lodestar", @"C:\Code\lode"]);

        await Assert.That(folders).IsEquivalentTo([@"C:\Code\Lode", @"C:\Code\Lodestar"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task CommonFolderHoldsThemAll()
    {
        await Assert.That(CodeCommands.CommonFolder([@"C:\Code\One", @"C:\Code\Two"])).IsEqualTo(@"C:\Code");
        await Assert.That(CodeCommands.CommonFolder([@"C:\Code\One", @"C:\Code\Group\Two"])).IsEqualTo(@"C:\Code");
        await Assert.That(CodeCommands.CommonFolder([@"C:\Code\Group\One", @"C:\Code\Group\Two"])).IsEqualTo(@"C:\Code\Group");
    }

    [Test]
    public async Task DiscardRestoresAndCleans()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");

        var results = await CodeCommands.Discard([repo]);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "untracked.txt"))).IsFalse();
    }

    // Every uncommitted change, as its prompt says: staged ones too, which the next Push would
    // otherwise commit, and untracked folders as well as files. Not what git ignores.
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

        var results = await CodeCommands.Discard([repo]);

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(repo, "added.txt"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(repo, "folder"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(repo, "ignored.txt"))).IsTrue();
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

    // Nor does a file git doesn't track stand in the way, though git then says there is nothing
    // to commit in other words.
    [Test]
    public async Task PushWithOnlyUntrackedFilesStillPushes()
    {
        using var code = new Scratch();
        var repo = code.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        Scratch.Git(repo, "commit", "-am", "Earlier");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");

        var results = await CodeCommands.Push([repo], "Unused");

        await Assert.That(results.Single().Succeeded).IsTrue();
        await Assert.That(Scratch.Git(code.Origin("Repo"), "log", "-1", "--format=%s")).IsEqualTo("Earlier");
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
            Cancel.None,
            item =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                // The later ones quicker, so they finish first.
                Thread.Sleep(400 - items.IndexOf(item) * 20);
                Interlocked.Decrement(ref running);
                return item;
            });

        await Assert.That(results).IsEquivalentTo(items, TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
            });

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
                ]));

    [Test]
    public Task OutcomeWhenAllSucceed() =>
        Verify(
            CodeCommands.GitOutcome(
                [
                    new(@"C:\Code\One", true, "Already up to date."),
                    new(@"C:\Code\Two", true, "Fast-forward")
                ]));

    [Test]
    public Task OutcomeWithNoRepos() =>
        Verify(CodeCommands.GitOutcome([]));

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
                ]));

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
        Verify(CodeCommands.NugetOutcome(@"C:\Code\CountryData", [], []));

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
            });

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

    internal sealed class Scratch : IDisposable
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

        // A repository with one committed file, tracking a bare origin it has pushed to.
        public string CreateRepo(string name)
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
            return repo;
        }

        public string Clone(string name, string cloneName)
        {
            var clone = At(cloneName);
            Git(Root, "clone", Origin(name), clone);
            Identify(clone);
            return clone;
        }

        static void Identify(string repo)
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
