/// <summary>
/// Shows what changed in a file: an uncommitted change, from the Changes pane, against the last
/// commit, or a commit's, from History, against the commit before. In the text editor chosen in
/// Options where it shows changes, VS Code say; otherwise in git's own diff tool, where one is
/// set in git's config; otherwise the file is opened as it is now, or as the commit left it.
/// </summary>
static class GitDiff
{
    /// <summary>
    /// Where the versions of files a commit had are written, for the editor to show beside each
    /// other. Tests put it somewhere of their own.
    /// </summary>
    internal static string Folder { get; set; } = Path.Combine(Path.GetTempPath(), "Lode", "Diffs");

    /// <summary>
    /// The uncommitted change to <paramref name="change"/>: the file as the last commit has it
    /// beside the file itself, which can be edited there.
    /// </summary>
    public static async void OfWorkingFile(IWin32Window owner, Settings settings, string repo, Change change)
    {
        var current = Path.Combine(repo, change.Path.Replace('/', '\\'));
        var oldPath = change.OldPath ?? change.Path;
        string? before = null;
        if (change.Kind is not (GitChange.Untracked or GitChange.Added))
        {
            before = await Task.Run(() => Version(repo, "HEAD", oldPath));
        }

        if (change.Kind == GitChange.Deleted)
        {
            OpenOne(owner, settings, before);
            return;
        }

        await Show(owner, settings, repo, before, current, ["HEAD", "--", change.Path]);
    }

    /// <summary>
    /// What <paramref name="commit"/> changed in <paramref name="change"/>: the file as the
    /// commit before it had it beside the file as the commit left it.
    /// </summary>
    public static async void OfCommit(IWin32Window owner, Settings settings, string repo, GitCommit commit, Change change)
    {
        string? parent = null;
        if (commit.Parents.Count > 0)
        {
            parent = commit.Parents[0];
        }

        var oldPath = change.OldPath ?? change.Path;
        var (before, after) = await Task.Run(() =>
        {
            string? left = null;
            if (parent != null &&
                change.Kind != GitChange.Added)
            {
                left = Version(repo, parent, oldPath);
            }

            string? right = null;
            if (change.Kind != GitChange.Deleted)
            {
                right = Version(repo, commit.Sha, change.Path);
            }

            return (left, right);
        });

        if (after == null)
        {
            OpenOne(owner, settings, before);
            return;
        }

        // The first commit has nothing before it for git's tool to compare.
        string[]? toolArguments = null;
        if (parent != null)
        {
            toolArguments = [parent, commit.Sha, "--", change.Path];
        }

        await Show(owner, settings, repo, before, after, toolArguments);
    }

    static async Task Show(IWin32Window owner, Settings settings, string repo, string? before, string after, string[]? toolArguments)
    {
        if (before == null ||
            !TextEditors.IsText(after))
        {
            OpenOne(owner, settings, after);
            return;
        }

        if (TextEditors.OpenDiff(owner, settings, before, after))
        {
            return;
        }

        if (toolArguments != null &&
            await Task.Run(() => HasDiffTool(repo)))
        {
            // Not waited for: the tool stays open as long as the user likes.
            _ = Task.Run(() => CodeCommands.Git(repo, ["--no-optional-locks", "difftool", "-y", .. toolArguments]));
            return;
        }

        OpenOne(owner, settings, after);
    }

    // Opens a file on its own: text in the editor, anything else as Windows opens it.
    static void OpenOne(IWin32Window owner, Settings settings, string? path)
    {
        if (path == null)
        {
            return;
        }

        if (TextEditors.IsText(path))
        {
            TextEditors.Open(owner, settings, [path]);
            return;
        }

        FileOperations.Open(owner, path);
    }

    // Unset, git difftool goes through a list of tools, vimdiff among them, which would wait in a
    // hidden console for keys nobody can press.
    static bool HasDiffTool(string repo)
    {
        foreach (var key in (string[]) ["diff.guitool", "diff.tool"])
        {
            var result = CodeCommands.GitOutput(repo, Cancel.None, ["config", "--get", key]);
            if (result.Succeeded &&
                result.Output.Trim().Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes <paramref name="path"/> as <paramref name="revision"/> has it to a file of the same
    /// name, so the editor colours it as it would the file, in a folder for the repository and
    /// commit, read only, as editing it changes nothing. As git would check it out, with the
    /// line endings and filters git's config says, so only what the commit changed shows as
    /// changed. Null when git can't write it.
    /// </summary>
    internal static string? Version(string repo, string revision, string path)
    {
        try
        {
            // By commit, as what HEAD is moves with every commit.
            if (!IsSha(revision))
            {
                var resolved = CodeCommands.GitOutput(repo, Cancel.None, ["rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}"]);
                if (!resolved.Succeeded)
                {
                    return null;
                }

                revision = resolved.Output.Trim();
            }

            var folder = Path.Combine(Folder, Hash(repo.ToLowerInvariant()), Hash($"{revision}\0{path}")[..12]);
            var file = Path.Combine(folder, Path.GetFileName(path));
            if (File.Exists(file))
            {
                return file;
            }

            Directory.CreateDirectory(folder);
            var writing = file + ".writing";
            if (!CodeCommands.GitToFile(repo, writing, Cancel.None, "--no-optional-locks", "cat-file", "--filters", $"{revision}:{path}"))
            {
                File.Delete(writing);
                return null;
            }

            File.Move(writing, file, true);
            File.SetAttributes(file, FileAttributes.ReadOnly);
            return file;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    static bool IsSha(string revision) =>
        revision.Length is 40 or 64 &&
        revision.All(char.IsAsciiHexDigitLower);
}
