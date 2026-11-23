using LibGit2Sharp;

/// <summary>
/// Reads a repository's state in Lode's own process, with LibGit2Sharp, rather than by running
/// git. Finds the same changes as <see cref="GitState.FromGit"/> does.
/// </summary>
static class LibGitState
{
    // Ignored files don't count, and nor do folders git doesn't track looked into: one is one
    // change, as git status shows it. Nor submodules at all: libgit2 can't leave out only what is
    // uncommitted inside one, as git does, without opening every one.
    static StatusOptions statusOptions = new()
    {
        IncludeIgnored = false,
        IncludeUntracked = true,
        RecurseUntrackedDirs = false,
        ExcludeSubmodules = true
    };

    /// <summary>
    /// The repository is let go of before this returns, so none of its files stay open, as
    /// Windows would then not let them, or the folder, be deleted.
    /// </summary>
    public static GitState? FromLibGit2(string repo, Cancel cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            using var repository = new Repository(repo);
            var head = repository.Head;
            string? branch = null;
            if (!repository.Info.IsHeadDetached)
            {
                branch = head.FriendlyName;
            }

            // Paths come with Windows' backslashes, and a folder git doesn't track with a slash
            // after it, both of which git status leaves as git has them.
            var files = repository
                .RetrieveStatus(statusOptions)
                .Where(_ => _.State is not (FileStatus.Unaltered or FileStatus.Ignored))
                .Select(_ => new GitFile(_.FilePath.Replace('\\', '/').TrimEnd('/'), ChangeOf(_.State)));
            var tracking = head.TrackingDetails;
            return new(branch, head.Tip?.Sha, tracking.AheadBy ?? 0, tracking.BehindBy ?? 0, GitState.InOrder(files))
            {
                Upstream = head.IsTracking,
                ForkBehind = ForkBehind(repository, head.Tip)
            };
        }
        // Not a repository libgit2 will take, or libgit2 itself not loaded: its native library
        // missing, or not for this processor.
        catch (Exception exception)
            when (
                exception is
                    LibGit2SharpException or
                    DllNotFoundException or
                    TypeInitializationException or
                    BadImageFormatException)
        {
            return null;
        }
    }

    // As GitState.FromGit counts it: the commits upstream's HEAD has that the one checked out
    // hasn't. None without a HEAD recorded for upstream, which only a fork has.
    static int ForkBehind(Repository repository, Commit? tip)
    {
        if (tip == null ||
            repository.Refs["refs/remotes/upstream/HEAD"]?.ResolveToDirectReference()?.Target is not Commit upstream)
        {
            return 0;
        }

        return repository.ObjectDatabase.CalculateHistoryDivergence(tip, upstream).BehindBy ?? 0;
    }

    /// <summary>
    /// Which of <paramref name="folders"/>, in the repository, git ignores, and of the folders
    /// each is in, down from the repository, as far as the first that it does: everything in
    /// that one is ignored with it. A folder ignored that holds a file git tracks all the same,
    /// one added by force say, is not given as ignored, as a change to that file is a change.
    /// Each by its path. All as not ignored where libgit2 can't read the repository.
    /// </summary>
    public static Dictionary<string, bool> Ignored(string repo, IReadOnlyCollection<string> folders)
    {
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var repository = new Repository(repo);
            foreach (var folder in folders)
            {
                var current = repo;
                foreach (var part in Path.GetRelativePath(repo, folder).Split(Path.DirectorySeparatorChar))
                {
                    current = Path.Combine(current, part);
                    if (!found.TryGetValue(current, out var ignored))
                    {
                        ignored = repository.Ignore.IsPathIgnored(Relative(repo, current));
                        found[current] = ignored;
                    }

                    if (ignored)
                    {
                        break;
                    }
                }
            }

            var roots = found
                .Where(_ => _.Value)
                .Select(_ => (Folder: _.Key, Prefix: Relative(repo, _.Key)))
                .ToList();
            if (roots.Count == 0)
            {
                return found;
            }

            foreach (var entry in repository.Index)
            {
                var path = entry.Path.Replace('\\', '/');
                foreach (var (folder, prefix) in roots)
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        found[folder] = false;
                    }
                }
            }
        }
        catch (Exception exception)
            when (
                exception is
                    LibGit2SharpException or
                    DllNotFoundException or
                    TypeInitializationException or
                    BadImageFormatException or
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            foreach (var folder in folders)
            {
                found[folder] = false;
            }
        }

        return found;
    }

    // As git has a folder's path: from the top of the repository, with forward slashes, and
    // one after it.
    static string Relative(string repo, string folder) =>
        Path.GetRelativePath(repo, folder).Replace('\\', '/') + '/';

    const FileStatus inWorkdir = FileStatus.ModifiedInWorkdir | FileStatus.TypeChangeInWorkdir | FileStatus.RenamedInWorkdir;

    // As GitState.ChangeOf has it from git: what isn't staged first, then what is.
    static GitChange ChangeOf(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Conflicted))
        {
            return GitChange.Conflicted;
        }

        if (state.HasFlag(FileStatus.NewInWorkdir))
        {
            return GitChange.Untracked;
        }

        if (state.HasFlag(FileStatus.DeletedFromWorkdir))
        {
            return GitChange.Deleted;
        }

        if ((state & inWorkdir) != 0)
        {
            return GitChange.Modified;
        }

        if (state.HasFlag(FileStatus.NewInIndex))
        {
            return GitChange.Added;
        }

        if (state.HasFlag(FileStatus.RenamedInIndex))
        {
            return GitChange.Renamed;
        }

        if (state.HasFlag(FileStatus.DeletedFromIndex))
        {
            return GitChange.Deleted;
        }

        return GitChange.Staged;
    }

    // From how many files on, git reads a repository faster than LibGit2Sharp: git looks over
    // the files on several threads, where libgit2 has one, which outweighs starting git. For a
    // code folder of 91 repositories, two of them over 10,000 files, reading all of them took
    // 341 ms so, where git took 590 ms and LibGit2Sharp 534 ms.
    internal const int manyFiles = 2_000;

    /// <summary>
    /// Whichever reads the repository faster: LibGit2Sharp for one of fewer than
    /// <see cref="manyFiles"/> files, which saves starting git, otherwise git. Git too for one
    /// libgit2 can't read, of a format newer than it knows say.
    /// </summary>
    public static GitState? FromFaster(string repo, Cancel cancel)
    {
        if (IndexEntries(repo) < manyFiles &&
            FromLibGit2(repo, cancel) is { } state)
        {
            return state;
        }

        return GitState.FromGit(repo, cancel);
    }

    /// <summary>
    /// How many files the repository's index holds, from its header: "DIRC", the version, then
    /// the count, each four bytes, most significant first. Of a worktree, its own index, in the
    /// folder its .git file names. Zero when it can't be read, a repository with nothing staged
    /// yet say.
    /// </summary>
    internal static int IndexEntries(string repo)
    {
        try
        {
            var directory = Path.Combine(repo, ".git");
            if (File.Exists(directory))
            {
                const string prefix = "gitdir: ";
                var pointer = File.ReadAllText(directory).Trim();
                if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return 0;
                }

                directory = Path.GetFullPath(Path.Combine(repo, pointer[prefix.Length..]));
            }

            // Shared for writing and deleting, so git, replacing the index meanwhile, isn't stopped.
            using var stream = new FileStream(Path.Combine(directory, "index"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("DIRC"u8))
            {
                return 0;
            }

            return BinaryPrimitives.ReadInt32BigEndian(header[8..]);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
