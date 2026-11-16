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

        await Assert.That(several.Reports).IsEquivalentTo(["1 of 2: One", "2 of 2: Two"]);
        await Assert.That(single.Reports).IsEmpty();
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
