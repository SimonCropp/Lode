/// <summary>
/// Git purge, run for real against throwaway repositories in the temp folder. Only moving to the
/// Recycle Bin is stood in for, so a test run leaves nothing there.
/// </summary>
public class GitPurgeTests
{
    [Test]
    public async Task AFolderInARepoIsJustItself()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Repo", ".git"));
        Directory.CreateDirectory(code.At("Repo", "src", "Deep"));

        await Assert.That(Folders(code, "Repo")).IsEquivalentTo(["Repo"]);
        await Assert.That(Folders(code, "Repo", "src", "Deep")).IsEquivalentTo(["Repo/src/Deep"]);
    }

    // However deep, but not into a repository: one nested in another is left alone.
    [Test]
    public async Task OutsideARepoEveryRepoBelow()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("One", "Nested", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));
        Directory.CreateDirectory(code.At("Group", "Deeper", "Still", "Three", ".git"));
        Directory.CreateDirectory(code.At("NotARepo", "Empty"));

        await Assert.That(Folders(code)).IsEquivalentTo(["Group/Deeper/Still/Three", "Group/Two", "One"]);
    }

    // The code folder may sit in a home folder kept in git, which would make everything in it
    // look untracked.
    [Test]
    public async Task ARepoAboveTheCodeFolderDoesNotCount()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(scratch.At(".git"));
        Directory.CreateDirectory(scratch.At("Code", "Repo", ".git"));
        Directory.CreateDirectory(scratch.At("Code", "Group"));
        var codeFolder = scratch.At("Code");

        await Assert.That(GitPurge.Folders(scratch.At("Code", "Group"), codeFolder)).IsEmpty();
        await Assert.That(GitPurge.Folders(codeFolder, codeFolder).Select(scratch.Relative)).IsEquivalentTo(["Code/Repo"]);
    }

    // Several folders: what each would purge, less what purging another already covers. A
    // repository nested in another isn't covered, as a purge leaves it alone.
    [Test]
    public async Task SeveralFoldersArePurgedOnceEach()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Repo", ".git"));
        Directory.CreateDirectory(code.At("Repo", "src", "Deep"));
        Directory.CreateDirectory(code.At("Repo", "Nested", ".git"));
        Directory.CreateDirectory(code.At("Group", "One", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));

        var folders = GitPurge.Folders(
            [
                code.At("Repo", "src", "Deep"),
                code.At("Repo"),
                code.At("Repo", "Nested"),
                code.At("Group"),
                code.At("Group", "One")
            ],
            code.Root);

        await Assert.That(folders.Select(code.Relative)).IsEquivalentTo(["Group/One", "Group/Two", "Repo", "Repo/Nested"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    static List<string> Folders(CodeCommandsTests.Scratch code, params string[] parts) =>
        GitPurge.Folders(code.At(parts), code.Root)
            .Select(code.Relative)
            .ToList();

    [Test]
    public async Task FindsWhatGitDoesNotTrack()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");

        await Verify(
            new
            {
                repo = Names(GitPurge.Find(repo)),
                src = Names(GitPurge.Find(Path.Combine(repo, "src"))),
                // A folder git ignores: what is in it, not the folder itself.
                bin = Names(GitPurge.Find(Path.Combine(repo, "bin")))
            });
    }

    static List<string> Names(GitPurge.Found found) =>
        found.Entries
            .Select(_ => _.Name)
            .ToList();

    // Untracked and ignored files and folders go, read-only ones too. Tracked files, and the
    // repository nested inside, stay.
    [Test]
    public async Task PurgingDeletesOutright()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");
        File.SetAttributes(Path.Combine(repo, "bin", "Debug", "Repo.dll"), FileAttributes.ReadOnly);

        var outcome = await GitPurge.Run([repo], permanent: true, IntPtr.Zero);

        await Verify(
            new
            {
                outcome,
                left = Left(repo)
            });
    }

    // Below the top of a repository, only what is in that folder goes.
    [Test]
    public async Task PurgingAFolderInARepo()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");

        var outcome = await GitPurge.Run([Path.Combine(repo, "src")], permanent: true, IntPtr.Zero);

        await Verify(
            new
            {
                outcome,
                left = Left(repo)
            });
    }

    // Every repository's at once, in one go to the Recycle Bin: here a stand-in, which turns one
    // file down as a file in use would be.
    [Test]
    public async Task PurgingToTheRecycleBin()
    {
        using var code = new CodeCommandsTests.Scratch();
        var one = Untidy(code, "One");
        var two = Untidy(code, "Two");
        var calls = new List<List<string>>();

        var outcome = await GitPurge.Run(
            [one, two],
            (paths, _) =>
            {
                calls.Add(paths.Select(code.Relative).ToList());
                IReadOnlyList<GitPurge.Failure> failures = [new(Path.Combine(two, "src", "build.log"), "The file is in use.")];
                return Task.FromResult(failures);
            },
            permanent: false);

        await Verify(
            new
            {
                calls,
                outcome
            });
    }

    [Test]
    public async Task NothingToPurge()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");

        var outcome = await GitPurge.Run([repo], permanent: true, IntPtr.Zero);

        await Assert.That(outcome).IsEqualTo(new(true, "Nothing to purge", ""));
    }

    // Git's complaint is passed on, and the other folders are still purged.
    [Test]
    public async Task AFolderGitCannotLookIn()
    {
        using var code = new CodeCommandsTests.Scratch();
        var notARepo = code.At("NotARepo");
        Directory.CreateDirectory(notARepo);
        var repo = code.CreateRepo("Repo");
        Write(repo, "Untracked.txt", "new");

        var outcome = await GitPurge.Run([notARepo, repo], permanent: true, IntPtr.Zero);

        await Verify(outcome);
    }

    // A repository with something of each kind: tracked, untracked, ignored, read-only, an empty
    // folder, a repository nested inside, and a name git quotes unless told not to.
    static string Untidy(CodeCommandsTests.Scratch code, string name)
    {
        var repo = code.CreateRepo(name);
        Write(repo, ".gitignore", "bin/\n*.log\n");
        Write(repo, "src/Tracked.cs", "class Tracked;");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Ignore builds");

        Write(repo, "bin/Debug/Repo.dll", "binary");
        Write(repo, "src/build.log", "log");
        Write(repo, "src/Untracked.cs", "class Untracked;");
        Write(repo, "src/obj/Repo.assets.json", "{}");
        Write(repo, "src/ünï cödé.txt", "unicode");
        Directory.CreateDirectory(Path.Combine(repo, "Empty"));

        var nested = Path.Combine(repo, "Nested");
        Directory.CreateDirectory(nested);
        CodeCommandsTests.Scratch.Git(nested, "init");
        Write(nested, "Untracked.txt", "kept");
        return repo;
    }

    static void Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Everything under the repository but git's own folders.
    static List<string> Left(string repo) =>
        Directory.EnumerateFileSystemEntries(repo, "*", SearchOption.AllDirectories)
            .Select(_ => Path.GetRelativePath(repo, _).Replace('\\', '/'))
            .Where(_ => !IsGitFolder(_))
            .Order(StringComparer.Ordinal)
            .ToList();

    static bool IsGitFolder(string relative) =>
        relative == ".git" ||
        relative.StartsWith(".git/", StringComparison.Ordinal) ||
        relative.EndsWith("/.git", StringComparison.Ordinal) ||
        relative.Contains("/.git/", StringComparison.Ordinal);
}
