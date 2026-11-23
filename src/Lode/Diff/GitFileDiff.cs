/// <summary>
/// What a diff is of: a file's uncommitted change, against the last commit, or, with
/// <paramref name="Commit"/>, what that commit changed in it, against the commit before.
/// <paramref name="HasCommits"/> is false before the repository's first commit, when everything
/// is new; <paramref name="Merging"/> true part way through a merge, when git commits
/// everything, so no lines are picked.
/// </summary>
sealed record DiffSource(string Repo, Change File, GitCommit? Commit = null, bool HasCommits = true, bool Merging = false)
{
    public bool IsUncommitted => Commit == null;

    /// <summary>
    /// Whether this is the same file of the same change as <paramref name="other"/>, to keep the
    /// place in it when it is read again.
    /// </summary>
    public bool SameFile(DiffSource? other) =>
        other != null &&
        Locations.Same(Repo, other.Repo) &&
        File.Path == other.File.Path &&
        Commit?.Sha == other.Commit?.Sha;
}

/// <summary>
/// Reads a file's diff from git, as GitHub Desktop's lib/git/diff.ts does: git diff against the
/// last commit for an uncommitted change, git diff --no-index against nothing for a file git
/// doesn't track, and git log -p for a commit's, a merge's against its first parent. Never git's
/// external diff tool, colours, or a textconv driver's text, which lines picked to commit
/// couldn't be applied from. An image git calls binary is read as it was and as it is.
/// </summary>
static class GitFileDiff
{
    /// <summary>
    /// The images GDI+ can draw, shown beside each other rather than said to be binary.
    /// </summary>
    static HashSet<string> imageExtensions =
    [
        with(StringComparer.OrdinalIgnoreCase),
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".ico",
        ".tif",
        ".tiff"
    ];

    static bool IsImage(string path) =>
        imageExtensions.Contains(Path.GetExtension(path));

    // An empty line of context is written as a space, whatever diff.suppressBlankEmpty says, so
    // it isn't taken for the end of the hunk.
    static string[] prefix =
    [
        "--no-optional-locks",
        "-c",
        "diff.suppressBlankEmpty=false",
        "-c",
        "core.quotePath=false"
    ];

    static IEnumerable<string> Options(bool hideWhitespace)
    {
        yield return "--no-ext-diff";
        yield return "--no-color";
        yield return "--no-textconv";
        if (hideWhitespace)
        {
            yield return "-w";
        }
    }

    /// <summary>
    /// Reads the diff of <paramref name="source"/>, whitespace changes left out when
    /// <paramref name="hideWhitespace"/>. Text too big to show straight away is a
    /// <see cref="LargeDiff"/>, unless <paramref name="evenIfLarge"/>.
    /// </summary>
    public static FileDiff Read(DiffSource source, bool hideWhitespace, bool evenIfLarge, Cancel cancel)
    {
        var patch = ReadPatch(source, hideWhitespace, cancel, out var error);
        if (patch == null)
        {
            return new FailedDiff(error);
        }

        var diff = DiffParser.Parse(patch, evenIfLarge);
        if (diff is BinaryDiff &&
            IsImage(source.File.Path))
        {
            return ReadImages(source);
        }

        return diff;
    }

    /// <summary>
    /// What git writes for the diff of <paramref name="source"/>, or null, with what it said,
    /// when it couldn't.
    /// </summary>
    static string? ReadPatch(DiffSource source, bool hideWhitespace, Cancel cancel, out string error)
    {
        var file = source.File;
        var paths = new List<string>
        {
            file.Path
        };
        if (file.OldPath != null &&
            file.OldPath != file.Path)
        {
            paths.Add(file.OldPath);
        }

        string[] arguments;
        var noIndex = false;
        if (source.Commit is { } commit)
        {
            arguments =
            [
                .. prefix,
                "log",
                commit.Sha,
                "-m",
                "-1",
                "--first-parent",
                "--format=",
                "-p",
                "-M",
                .. Options(hideWhitespace),
                "--",
                .. paths
            ];
        }
        else if (file.Kind == GitChange.Untracked ||
                 !source.HasCommits)
        {
            // Against nothing: git has no version of it to compare with.
            noIndex = true;
            if (!File.Exists(Path.Combine(source.Repo, file.Path)))
            {
                error = "";
                return "";
            }

            arguments =
            [
                .. prefix,
                "diff",
                "--no-index",
                .. Options(hideWhitespace),
                "--",
                "/dev/null",
                file.Path
            ];
        }
        else
        {
            arguments =
            [
                .. prefix,
                "diff",
                "-M",
                .. Options(hideWhitespace),
                "HEAD",
                "--",
                .. paths
            ];
        }

        var read = CodeCommands.GitOutput(source.Repo, cancel, arguments);
        error = read.Error;
        if (read.Succeeded ||
            (noIndex && read.ExitCode == 1 && !cancel.IsCancellationRequested))
        {
            return read.Output;
        }

        if (error.Length == 0)
        {
            error = $"git exited with {read.ExitCode}";
        }

        return null;
    }

    // The image as the commit before had it and as it is now, or as the commit left it. Neither
    // for one added or deleted, nor where git couldn't write it.
    static FileDiff ReadImages(DiffSource source)
    {
        var file = source.File;
        var oldPath = file.OldPath ?? file.Path;
        byte[]? before = null;
        byte[]? after = null;
        if (source.Commit is { } commit)
        {
            if (commit.Parents.Count > 0 &&
                file.Kind != GitChange.Added)
            {
                before = ReadVersion(source.Repo, commit.Parents[0], oldPath);
            }

            if (file.Kind != GitChange.Deleted)
            {
                after = ReadVersion(source.Repo, commit.Sha, file.Path);
            }
        }
        else
        {
            if (source.HasCommits &&
                file.Kind is not (GitChange.Untracked or GitChange.Added))
            {
                before = ReadVersion(source.Repo, "HEAD", oldPath);
            }

            if (file.Kind != GitChange.Deleted)
            {
                after = ReadBytes(Path.Combine(source.Repo, file.Path.Replace('/', '\\')));
            }
        }

        if (before == null &&
            after == null)
        {
            return new BinaryDiff();
        }

        return new ImageDiff(before, after);
    }

    static byte[]? ReadVersion(string repo, string revision, string path)
    {
        var written = GitDiff.Version(repo, revision, path);
        if (written == null)
        {
            return null;
        }

        return ReadBytes(written);
    }

    static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
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

    /// <summary>
    /// The lines of the file the diff shows changed, as it is now or as the commit left it, for
    /// arrows that show more of it. Null for a file deleted, too big, or that can't be read, when
    /// there are no arrows.
    /// </summary>
    public static IReadOnlyList<string>? NewLines(DiffSource source)
    {
        var file = source.File;
        if (file.Kind == GitChange.Deleted)
        {
            return null;
        }

        string? path;
        if (source.Commit is { } commit)
        {
            path = GitDiff.Version(source.Repo, commit.Sha, file.Path);
        }
        else
        {
            path = Path.Combine(source.Repo, file.Path.Replace('/', '\\'));
        }

        if (path == null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                info.Length > DiffExpansion.MaxFileLength)
            {
                return null;
            }

            return DiffExpansion.LinesOf(File.ReadAllText(path));
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
}
