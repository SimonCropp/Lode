/// <summary>
/// The files a commit changed, read in process as git diff-tree -M lists them, git the oracle:
/// in folders and out, of every mode, and renames found as git's diffcore-rename finds them.
/// </summary>
public class CommitFilesTests
{
    static void Git(string repo, params string[] arguments) =>
        CodeCommandsTests.Scratch.Git(repo, arguments);

    static List<string> Lines(List<Change>? files)
    {
        if (files == null)
        {
            return ["none"];
        }

        return files
            .Select(_ => $"{_.Kind} {_.Path} {_.OldPath}".Trim())
            .ToList();
    }

    // Each commit of the history's files, as git lists them.
    static async Task FilesAgree(string repo, int count = 20)
    {
        foreach (var commit in GitHistory.Read(repo, 0, count, Cancel.None)!)
        {
            await Assert.That(Lines(InProcessHistory.FilesOf(repo, commit, Cancel.None))).IsEquivalentTo(Lines(GitHistory.FilesOf(repo, commit, Cancel.None)), CollectionOrdering.Matching);
        }
    }

    static void Commit(string repo, string message)
    {
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "--allow-empty", "-m", message);
    }

    // Files changed deep in folders and left alone beside them; a folder deleted whole, and one
    // added; a file becoming a folder of the same name and back; names one the start of another.
    [Test]
    public async Task ListsChangesInFoldersAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        foreach (var path in new[] { "a/b/c/deep.txt", "a/b/kept.txt", "a/other.txt", "gone/one.txt", "gone/inner/two.txt", "swap", "a.txt", "a-b.txt", "ab/x.txt" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, path))!);
            await File.WriteAllTextAsync(Path.Combine(repo, path), path);
        }

        Commit(repo, "Files");
        await File.WriteAllTextAsync(Path.Combine(repo, "a", "b", "c", "deep.txt"), "changed");
        Directory.Delete(Path.Combine(repo, "gone"), true);
        Directory.CreateDirectory(Path.Combine(repo, "added", "inner"));
        await File.WriteAllTextAsync(Path.Combine(repo, "added", "inner", "new.txt"), "new file");
        File.Delete(Path.Combine(repo, "swap"));
        Directory.CreateDirectory(Path.Combine(repo, "swap"));
        await File.WriteAllTextAsync(Path.Combine(repo, "swap", "inside.txt"), "now a folder");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "changed too");
        Commit(repo, "Changes");
        Directory.Delete(Path.Combine(repo, "swap"), true);
        await File.WriteAllTextAsync(Path.Combine(repo, "swap"), "a file again");
        Commit(repo, "Back");

        // A name for one the start of the name for the other.
        File.Move(Path.Combine(repo, "a.txt"), Path.Combine(repo, "a.txt.old"));
        Commit(repo, "Longer");

        await FilesAgree(repo);
    }

    // Modes as git has them: run and not, a link, a submodule, and an old 664 git takes as 644;
    // a change of mode only, and of type.
    [Test]
    public async Task ListsModesAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var head = GitRun.Output(repo, "rev-parse", "HEAD");
        var blob = GitRun.Output(repo, "rev-parse", "HEAD:tracked.txt");
        Git(repo, "update-index", "--add", "--cacheinfo", $"100755,{blob},run.sh");
        Git(repo, "update-index", "--add", "--cacheinfo", $"120000,{blob},link");
        Git(repo, "update-index", "--add", "--cacheinfo", $"160000,{head},module");
        Git(repo, "update-index", "--add", "--cacheinfo", $"100644,{blob},plain.txt");
        Git(repo, "commit", "-q", "-m", "Modes");
        Git(repo, "update-index", "--cacheinfo", $"100644,{blob},run.sh");
        Git(repo, "update-index", "--cacheinfo", $"100644,{blob},link");
        Git(repo, "update-index", "--cacheinfo", $"120000,{blob},plain.txt");
        Git(repo, "commit", "-q", "-m", "Changed modes");

        // A tree as old git wrote one, 100664, which git takes as 100644: no change.
        var entries = GitRun.Output(repo, "ls-tree", "HEAD");
        var rewritten = new MemoryStream();
        foreach (var line in entries.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            var parts = line[..tab].Split(' ');
            var mode = parts[0];
            if (line[(tab + 1)..] == "tracked.txt")
            {
                mode = "100664";
            }

            rewritten.Write(Encoding.UTF8.GetBytes($"{mode.TrimStart('0')} {line[(tab + 1)..]}\0"));
            rewritten.Write(Convert.FromHexString(parts[2]));
        }

        var oldTree = GitRun.WriteObject(repo, "tree", rewritten.ToArray());
        var commit = GitRun.WriteObject(repo, "commit", $"tree {oldTree}\nparent {GitRun.Output(repo, "rev-parse", "HEAD")}\nauthor A <a@example.com> 1600000000 +0000\ncommitter A <a@example.com> 1600000000 +0000\n\nOld mode\n");
        Git(repo, "update-ref", "refs/heads/main", commit);
        Git(repo, "reset", "-q");

        await FilesAgree(repo);
    }

    // A tree with a mode that isn't octal, or cut short, isn't one.
    [Test]
    [Arguments("100x44 a\0")]
    [Arguments("100644 no-nul")]
    [Arguments("100644 short\0abc")]
    public async Task RefusesBrokenTrees(string tree) =>
        await Assert.That(() => Trees.Read(Encoding.Latin1.GetBytes(tree))).Throws<GitFilesException>();

    // Renamed and changed, moved to another folder under the same name, as git looks for those
    // first; two of one name, which git then can't take by name; a hundred and more the same,
    // of which one is renamed; one renamed to two, the second then added; links and empty files,
    // renamed only where the same, a link not alike any file; files of CRLF lines, and binary.
    [Test]
    public async Task FindsRenamesAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "config", "core.autocrlf", "false");
        var lines = string.Join('\n', Enumerable.Range(0, 50).Select(_ => $"Line {_} of the file"));
        var crlf = lines.Replace("\n", "\r\n");
        var files = new Dictionary<string, string>
        {
            ["from/same-name.txt"] = lines,
            ["one/dup.txt"] = lines + "\none",
            ["two/dup.txt"] = lines + "\ntwo",
            ["crlf.txt"] = crlf,
            ["binary.bin"] = "\0binary\0" + lines,
            ["empty.txt"] = "",
            ["tiny.txt"] = "a",
            ["orig.txt"] = "the original, renamed twice",
            ["from/heavy.txt"] = lines,
            ["sub/same.txt"] = lines + "\nsuffix",
            ["prefixed.txt"] = lines + "\nprefixed",
            ["deep2.txt"] = lines + "\ndeep"
        };
        for (var index = 0; index < 105; index++)
        {
            files[$"same/copy{index:000}.txt"] = "identical";
        }

        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, path)) ?? repo);
            await File.WriteAllTextAsync(Path.Combine(repo, path), content);
        }

        // Links staged as git would stage them from Linux, none of them in the working tree.
        var blob = GitRun.WriteObject(repo, "blob", "target");
        var lonely = GitRun.WriteObject(repo, "blob", "a target of its own, alike nothing");
        Git(repo, "add", "-A");
        Git(repo, "update-index", "--add", "--cacheinfo", $"120000,{blob},link-from");
        Git(repo, "update-index", "--add", "--cacheinfo", $"120000,{lonely},lonely-link");
        Git(repo, "commit", "-q", "-m", "Files");

        Directory.CreateDirectory(Path.Combine(repo, "to"));
        File.Move(Path.Combine(repo, "from", "same-name.txt"), Path.Combine(repo, "to", "same-name.txt"));
        await File.AppendAllTextAsync(Path.Combine(repo, "to", "same-name.txt"), "\nAn edit");
        Directory.CreateDirectory(Path.Combine(repo, "three"));
        Directory.CreateDirectory(Path.Combine(repo, "four"));
        File.Move(Path.Combine(repo, "one", "dup.txt"), Path.Combine(repo, "three", "dup.txt"));
        File.Move(Path.Combine(repo, "two", "dup.txt"), Path.Combine(repo, "four", "dup.txt"));
        await File.AppendAllTextAsync(Path.Combine(repo, "three", "dup.txt"), "\nedited");
        await File.AppendAllTextAsync(Path.Combine(repo, "four", "dup.txt"), "\nedited");
        File.Move(Path.Combine(repo, "crlf.txt"), Path.Combine(repo, "crlf-moved.txt"));
        await File.AppendAllTextAsync(Path.Combine(repo, "crlf-moved.txt"), "\r\nMore");
        File.Move(Path.Combine(repo, "binary.bin"), Path.Combine(repo, "binary-moved.bin"));
        await File.AppendAllTextAsync(Path.Combine(repo, "binary-moved.bin"), "\nMore");
        File.Move(Path.Combine(repo, "empty.txt"), Path.Combine(repo, "empty-moved.txt"));
        File.Move(Path.Combine(repo, "tiny.txt"), Path.Combine(repo, "tiny-moved.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo, "tiny-moved.txt"), "b");
        // Two in five lines changed: too unlike to be taken by its name, alike enough after.
        var heavy = string.Join('\n', Enumerable.Range(0, 50).Select(HeavyLine));
        File.Delete(Path.Combine(repo, "from", "heavy.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo, "to", "heavy.txt"), heavy);

        // Its path the end of the other's, so of the same name, but too changed to be taken by it.
        File.Delete(Path.Combine(repo, "deep2.txt"));
        Directory.CreateDirectory(Path.Combine(repo, "inner"));
        await File.WriteAllTextAsync(Path.Combine(repo, "inner", "deep2.txt"), heavy + "\ndeep");
        File.Move(Path.Combine(repo, "sub", "same.txt"), Path.Combine(repo, "same.txt"));
        await File.AppendAllTextAsync(Path.Combine(repo, "same.txt"), "\nedited");
        File.Move(Path.Combine(repo, "prefixed.txt"), Path.Combine(repo, "xprefixed.txt"));
        await File.AppendAllTextAsync(Path.Combine(repo, "xprefixed.txt"), "\nedited");
        File.Move(Path.Combine(repo, "orig.txt"), Path.Combine(repo, "x1.txt"));
        File.Copy(Path.Combine(repo, "x1.txt"), Path.Combine(repo, "x2.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo, "linkblob.txt"), "target");
        foreach (var index in Enumerable.Range(0, 105))
        {
            File.Delete(Path.Combine(repo, "same", $"copy{index:000}.txt"));
        }

        await File.WriteAllTextAsync(Path.Combine(repo, "identical.txt"), "identical");
        Git(repo, "add", "-A");
        Git(repo, "update-index", "--add", "--cacheinfo", $"120000,{blob},link-to");
        Git(repo, "commit", "-q", "-m", "Renames");

        await FilesAgree(repo);

        Git(repo, "config", "diff.renameLimit", "2");
        await FilesAgree(repo);

        static string HeavyLine(int line)
        {
            if (line % 5 < 2)
            {
                return $"Rewritten {line}";
            }

            return $"Line {line} of the file";
        }
    }

    // A commit not there, or not a commit, or with a parent that isn't an id, can't be read, so
    // is left to git, which says what it can of it.
    [Test]
    public async Task LeavesCommitsItCantReadToGit()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var head = GitHistory.Read(repo, 0, 1, Cancel.None)![0];
        var tree = GitRun.Output(repo, "rev-parse", "HEAD^{tree}");
        GitCommit[] unreadable =
        [
            head with { Sha = "not an id" },
            head with { Sha = "0123456789abcdef0123456789abcdef01234567" },
            head with { Sha = tree },
            head with { Parents = ["not an id"] }
        ];
        foreach (var commit in unreadable)
        {
            await Assert.That(InProcessHistory.FilesOf(repo, commit, Cancel.None)).IsNull();
            await Assert.That(Lines(InProcessHistory.FilesOfFaster(repo, commit, Cancel.None))).IsEquivalentTo(Lines(GitHistory.FilesOf(repo, commit, Cancel.None)), CollectionOrdering.Matching);
        }

        await Assert.That(InProcessHistory.FilesOf(scratch.At("NotARepository"), head, Cancel.None)).IsNull();
    }
}
