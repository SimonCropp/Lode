/// <summary>
/// A working tree's state read in process as git status reads it, git the oracle, for what the
/// Git tests' common cases don't reach: many folders, conflicts and staging of odd kinds, files
/// git is told not to look at, links, and the index itself in every form git writes it.
/// </summary>
public class WorkingTreeTests
{
    static void Git(string repo, params string[] arguments) =>
        CodeCommandsTests.Scratch.Git(repo, arguments);

    // A repository of over 500 folders, each with a file, all committed: enough for them to be
    // listed on threads ahead.
    static async Task<string> ManyFolders(CodeCommandsTests.Scratch scratch)
    {
        var repo = scratch.CreateRepo("Repo");
        for (var index = 0; index < 520; index++)
        {
            var folder = Path.Combine(repo, $"f{index / 26}", $"g{index}");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), $"{index}");
        }

        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Folders");
        return repo;
    }

    // Over 500 folders, listed on threads ahead: some files changed, some new, one folder gone.
    [Test]
    public async Task ReadsManyFoldersAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = await ManyFolders(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, "f3", "g80", "file.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "f7", "g190", "new.txt"), "new");
        Directory.Delete(Path.Combine(repo, "f12", "g315"), true);
        Directory.Delete(Path.Combine(repo, "f19"), true);

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files.Count).IsGreaterThan(20);
    }

    // Over 500 folders, one of them now a file, the folder deleted and a file made in its place:
    // what was in it is gone, and the file is new, as where there are fewer folders, and it is
    // never listed. Listed ahead, it couldn't be, which stopped the read.
    [Test]
    public async Task ReadsManyFoldersWithOneNowAFileAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = await ManyFolders(scratch);
        var folder = Path.Combine(repo, "f3", "g80");
        Directory.Delete(folder, true);
        await File.WriteAllTextAsync(folder, "a file where a folder was");

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files.Count).IsEqualTo(2);
    }

    // Over 500 folders, one of them that can't be listed, as it is in use, or is a cloud
    // provider's that isn't running: left to git, rather than the read failing.
    [Test]
    public async Task LeavesManyFoldersWithOneThatCantBeListedToGit()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = await ManyFolders(scratch);
        await File.WriteAllTextAsync(Path.Combine(repo, "f3", "g80", "file.txt"), "changed");
        using var held = HeldAlone(Path.Combine(repo, "f7", "g190"));

        await GitRun.LeftToGit(repo);
    }

    // A folder opened to list, as nobody else may open it while it is: listing it fails, as in
    // use. Opened only for its attributes, it would let anyone.
    static Microsoft.Win32.SafeHandles.SafeFileHandle HeldAlone(string folder)
    {
        const uint listDirectory = 0x0001;
        var handle = NativeMethods.CreateFile(folder, listDirectory, 0, IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        return handle;
    }

    // Conflicts of every kind a merge leaves: both added in a folder new to both, one side
    // deleting what the other changed, its file deleted since, and in a folder deleted since.
    [Test]
    public async Task ReadsConflictsOfEveryKind()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Directory.CreateDirectory(Path.Combine(repo, "kept"));
        await File.WriteAllTextAsync(Path.Combine(repo, "kept", "changed.txt"), "base");
        await File.WriteAllTextAsync(Path.Combine(repo, "deleted.txt"), "base");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Base");
        Git(repo, "switch", "-q", "-c", "side");
        Directory.CreateDirectory(Path.Combine(repo, "new"));
        await File.WriteAllTextAsync(Path.Combine(repo, "new", "both.txt"), "side");
        await File.WriteAllTextAsync(Path.Combine(repo, "kept", "changed.txt"), "side");
        Git(repo, "rm", "-q", "deleted.txt");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Side");
        Git(repo, "switch", "-q", "main");
        Directory.CreateDirectory(Path.Combine(repo, "new"));
        await File.WriteAllTextAsync(Path.Combine(repo, "new", "both.txt"), "main");
        Git(repo, "rm", "-q", "kept/changed.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "deleted.txt"), "main");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Main");
        await Assert.That(GitRun.Run(repo, "merge", "side").Succeeded).IsFalse();
        var merged = await GitRun.Agree(repo);

        File.Delete(Path.Combine(repo, "deleted.txt"));
        Directory.Delete(Path.Combine(repo, "kept"), true);
        Directory.Delete(Path.Combine(repo, "new"), true);
        var gone = await GitRun.Agree(repo);

        await Assert.That(merged.Files.Count(_ => _.Change == GitChange.Conflicted)).IsEqualTo(3);
        await Assert.That(gone.Files.Count(_ => _.Change == GitChange.Conflicted)).IsEqualTo(3);
    }

    // Staged otherwise than by add: a file HEAD has, taken out and only meant to be added back;
    // one staged as a link; a submodule staged where a file is, and a file where a submodule is;
    // a repository where a file is staged; a file git is told is unchanged, or not to look at.
    // The link, checked out as a file, is a change of type only where git makes links, which
    // git init turns off where Windows makes none, and leaves on for an administrator, as CI's.
    [Test]
    [Arguments("false")]
    [Arguments("true")]
    public async Task ReadsWhatIsStagedOddlyAsGitDoes(string symlinks)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", "core.symlinks", symlinks);
        foreach (var file in new[] { "intended.txt", "link.txt", "sub.txt", "nested.txt", "committed.txt", "assumed.txt", "skipped.txt", "assumedgone.txt", "skippedgone.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(repo, file), file);
        }

        Directory.CreateDirectory(Path.Combine(repo, "assumedfolder"));
        await File.WriteAllTextAsync(Path.Combine(repo, "assumedfolder", "inside.txt"), "inside");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Files");

        // Nothing committed after this, so all of it is staged against HEAD.
        var head = GitRun.Output(repo, "rev-parse", "HEAD");
        var blob = GitRun.Output(repo, "rev-parse", "HEAD:link.txt");
        Git(repo, "rm", "-q", "--cached", "intended.txt");
        Git(repo, "add", "-N", "intended.txt");
        Git(repo, "update-index", "--cacheinfo", $"120000,{blob},link.txt");
        Git(repo, "update-index", "--cacheinfo", $"160000,{head},sub.txt");
        Git(repo, "update-index", "--add", "--cacheinfo", $"160000,{head},asfile");
        await File.WriteAllTextAsync(Path.Combine(repo, "asfile"), "a file where a submodule is");
        File.Delete(Path.Combine(repo, "nested.txt"));
        Directory.CreateDirectory(Path.Combine(repo, "nested.txt"));
        Git(Path.Combine(repo, "nested.txt"), "init", "-q");
        var committed = Path.Combine(repo, "committed.txt");
        File.Delete(committed);
        Directory.CreateDirectory(committed);
        Git(committed, "init", "-q");
        Git(committed, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "First");
        Git(repo, "update-index", "--assume-unchanged", "assumedfolder/inside.txt");
        Directory.Delete(Path.Combine(repo, "assumedfolder"), true);
        Git(repo, "update-index", "--assume-unchanged", "assumed.txt", "assumedgone.txt");
        Git(repo, "update-index", "--skip-worktree", "skipped.txt", "skippedgone.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "assumed.txt"), "changed but not looked at");
        await File.WriteAllTextAsync(Path.Combine(repo, "skipped.txt"), "changed but not looked at");
        File.Delete(Path.Combine(repo, "assumedgone.txt"));
        File.Delete(Path.Combine(repo, "skippedgone.txt"));

        await GitRun.Agree(repo);
    }

    // A junction is a folder to git for Windows, where a file is tracked or where nothing is; a
    // link, where Windows lets one be made, a file of its own.
    [Test]
    public async Task ReadsJunctionsAndLinksAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "was-a-file"), "file");
        await File.WriteAllTextAsync(Path.Combine(repo, "was-linked"), "file");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "File");
        File.Delete(Path.Combine(repo, "was-a-file"));
        File.Delete(Path.Combine(repo, "was-linked"));
        var target = scratch.At("Target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "inside.txt"), "inside");
        foreach (var junction in new[] { "was-a-file", "untracked-junction" })
        {
            await Assert.That(Link(Path.Combine(repo, junction), target, "/J")).IsTrue();
        }

        // Without developer mode, or administrator, Windows makes no link: then the file is gone.
        if (!Link(Path.Combine(repo, "was-linked"), Path.Combine(target, "inside.txt"), null))
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "was-linked"), "file");
        }

        await GitRun.Agree(repo);
    }

    // As mklink makes one: a junction with /J, which needs no privilege, a link without.
    static bool Link(string path, string target, string? kind)
    {
        var startInfo = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        if (kind != null)
        {
            startInfo.ArgumentList.Add(kind);
        }

        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    // Names longer than a short buffer holds, in letters beyond ASCII, as folders and files.
    [Test]
    public async Task ReadsLongNamesAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        // Past MAX_PATH, which git for Windows only goes beyond told to.
        Git(repo, "config", "core.longpaths", "true");
        var name = new string('é', 200);
        Directory.CreateDirectory(Path.Combine(repo, name));
        await File.WriteAllTextAsync(Path.Combine(repo, name, name + ".txt"), "long");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Long");
        await File.WriteAllTextAsync(Path.Combine(repo, name, name + ".txt"), "changed");

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files.Count).IsEqualTo(1);
    }

    // Times compared as core.checkStat and core.trustctime say: minimal, a file's creation time
    // not looked at, as git then doesn't.
    [Test]
    [Arguments("core.checkStat", "minimal")]
    [Arguments("core.trustctime", "false")]
    public async Task ComparesTimesAsConfigured(string key, string value)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", key, value);
        await File.WriteAllTextAsync(Path.Combine(repo, "second.txt"), "second");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Second");
        File.SetCreationTimeUtc(Path.Combine(repo, "second.txt"), DateTime.UtcNow.AddDays(-1));
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed!");

        await GitRun.Agree(repo);
    }

    // Renames found as status.renames, else diff.renames, says: not at all where turned off; and
    // a repository asking for copies, which aren't looked for here, left to git, as is one
    // where git compares the run bit of files.
    [Test]
    [Arguments("status.renames", "false", false)]
    [Arguments("diff.renames", "false", false)]
    [Arguments("status.renames", "true", false)]
    [Arguments("status.renames", "copies", true)]
    [Arguments("core.fileMode", "true", true)]
    public async Task FindsRenamesAsConfigured(string key, string value, bool leftToGit)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", key, value);
        Git(repo, "mv", "tracked.txt", "renamed.txt");

        if (leftToGit)
        {
            await GitRun.LeftToGit(repo);
            return;
        }

        await GitRun.Agree(repo);
    }

    // An index git splits, or one sparse checkout leaves sparse, isn't read here; one of version
    // 4 is, its paths each only what differs from the one before, however much that is.
    [Test]
    public async Task ReadsTheIndexAsGitWritesIt()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        // Past MAX_PATH, which git for Windows only goes beyond told to.
        Git(repo, "config", "core.longpaths", "true");
        var deep = string.Join('/', Enumerable.Range(0, 12).Select(_ => $"folder{_:00}longname"));
        foreach (var start in new[] { "a", "b" })
        {
            var folder = Path.Combine(repo, start, deep);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), start);
        }

        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Deep");
        Git(repo, "update-index", "--index-version", "4");
        await File.WriteAllTextAsync(Path.Combine(repo, "b", deep, "file.txt"), "changed");
        await GitRun.Agree(repo);

        // Apart: from version 4, git splits it only when asked on its own.
        Git(repo, "update-index", "--index-version", "2");
        Git(repo, "update-index", "--split-index");
        await GitRun.LeftToGit(repo);

        Git(repo, "update-index", "--no-split-index");
        Git(repo, "sparse-checkout", "set", "--cone", "--sparse-index", "a");
        await GitRun.LeftToGit(repo);
    }

    static void WriteIndex(string gitDir, byte[] bytes)
    {
        var path = Path.Combine(gitDir, "index");
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, bytes);
    }

    // An index cut short, of a version not known, flagged extended in version 2, a path without
    // its end, or a version 4 path dropping more than the one before had, is refused.
    [Test]
    [Arguments("short")]
    [Arguments("signature")]
    [Arguments("version")]
    [Arguments("count")]
    [Arguments("extended")]
    [Arguments("unended")]
    [Arguments("drop")]
    [Arguments("unended4")]
    public async Task RefusesBrokenIndexes(string breaking)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var gitDir = Path.Combine(repo, ".git");
        if (breaking is "drop" or "unended4")
        {
            Git(repo, "update-index", "--index-version", "4");
        }

        var bytes = await File.ReadAllBytesAsync(Path.Combine(gitDir, "index"));
        switch (breaking)
        {
            case "short":
                bytes = bytes[..10];
                break;
            case "signature":
                bytes[0] = (byte)'X';
                break;
            case "version":
                bytes[7] = 5;
                break;
            case "count":
                bytes[11] = 200;
                break;
            case "extended":
                bytes[12 + 60] |= 0x40;
                break;
            case "unended":
                Array.Fill(bytes, (byte)'x', 12 + 62, bytes.Length - 12 - 62);
                break;
            case "drop":
                bytes[12 + 62] = 0x7f;
                break;
            case "unended4":
                Array.Fill(bytes, (byte)'x', 12 + 63, bytes.Length - 12 - 63);
                break;
        }

        WriteIndex(gitDir, bytes);

        await Assert.That(() => IndexFile.Read(gitDir)).Throws<GitFilesException>();
        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
    }

    // A .gitattributes in a conflict and since deleted, that says how a file is hashed: git
    // reads ours, staged second, from the index, which has the file's CRLFs made LFs, so it
    // isn't what was staged.
    [Test]
    public async Task HashesByOurAttributesInAConflict()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "# base\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "lines.txt"), "one\r\ntwo\r\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "Base");
        Git(repo, "switch", "-q", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "# theirs\n");
        Git(repo, "commit", "-q", "-am", "Theirs");
        Git(repo, "switch", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "*.txt text\n");
        Git(repo, "commit", "-q", "-am", "Ours");
        await Assert.That(GitRun.Run(repo, "merge", "side").Succeeded).IsFalse();
        File.Delete(Path.Combine(repo, ".gitattributes"));
        File.SetLastWriteTimeUtc(Path.Combine(repo, "lines.txt"), DateTime.UtcNow.AddMinutes(-5));

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files).Contains(new GitFile("lines.txt", GitChange.Modified));
    }

    // Untracked files all, those ignored left out, in folders not tracked and below them.
    [Test]
    public async Task ListsUntrackedFilesAllButIgnored()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", "status.showUntrackedFiles", "all");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "*.log\n");
        Directory.CreateDirectory(Path.Combine(repo, "new", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(repo, "new", "kept.txt"), "kept");
        await File.WriteAllTextAsync(Path.Combine(repo, "new", "left.log"), "left out");
        await File.WriteAllTextAsync(Path.Combine(repo, "new", "deeper", "left.log"), "left out");

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files.Select(_ => _.Path)).IsEquivalentTo([".gitignore", "new/kept.txt"], CollectionOrdering.Matching);
    }

    // Folders of a repository nothing ignores, and a cache tree without the folder asked for.
    [Test]
    public async Task TakesNothingAsIgnoredWhereNothingIs()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var bin = Path.Combine(repo, "bin");
        var tree = new IndexFile.CacheTree
        {
            EntryCount = 1
        };

        await Assert.That(InProcessState.Ignored(repo, [bin])[bin]).IsFalse();
        await Assert.That(tree.Child("missing"u8)).IsNull();
    }

    // Cancelled before it starts, nothing is read.
    [Test]
    public async Task ReadsNothingCancelled()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        using var source = new CancelSource();
        await source.CancelAsync();

        await Assert.That(InProcessState.FromFiles(repo, source.Token)).IsNull();
        await Assert.That(InProcessHistory.Read(repo, 0, 10, source.Token)).IsNull();
    }

    // Which folders git ignores, as not ignored at all where the repository can't be read here.
    [Test]
    public async Task TakesNothingAsIgnoredInARepositoryItCantRead()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/\n");
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "[broken\n");
        var bin = Path.Combine(repo, "bin");
        var plain = scratch.At("Plain");
        Directory.CreateDirectory(plain);

        await Assert.That(InProcessState.Ignored(repo, [bin])[bin]).IsFalse();
        await Assert.That(InProcessState.Ignored(plain, [Path.Combine(plain, "bin")]).Values).IsEquivalentTo([false]);
    }
}
