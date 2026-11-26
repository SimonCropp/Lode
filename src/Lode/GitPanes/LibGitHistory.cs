using LibGit2Sharp;

/// <summary>
/// Reads a repository's history in Lode's own process, with LibGit2Sharp, rather than by running
/// git as <see cref="GitHistory"/> does: the same commits, the same ones not pushed, and the same
/// files each changed. Several times quicker for what History reads as it opens, as no git is
/// started: see HistoryBenchmarks. Each Faster reads in process, and runs git where libgit2 can't
/// read the repository, or can't read it as git would. The repository is let go of before each
/// returns, so none of its files stay open, as Windows would then not let them, or the folder, be
/// deleted.
/// </summary>
static class LibGitHistory
{
    // How far down the history libgit2 reads it quicker than git, which walks a long history
    // quicker with its commit graph: the two are level at 2,000 commits of a 10,000 commit
    // history, 19 ms against 21, against 10 and 21 for the first 200.
    const int deepest = 2_000;

    // Renames found, as diff-tree is asked to with -M, as many files looked at for them as git
    // looks at, where libgit2 would look at 200.
    static LibGit2Sharp.CompareOptions compareOptions = new()
    {
        Similarity = new()
        {
            RenameDetectionMode = RenameDetectionMode.Renames,
            RenameLimit = 1_000
        }
    };

    /// <summary>
    /// The commits from HEAD back, newest first, after the first <paramref name="skip"/>, as
    /// <see cref="GitHistory.Read(string, int, int, Cancel)"/> reads them: in process down to
    /// 2,000 commits, by git further down.
    /// </summary>
    public static List<GitCommit>? ReadFaster(string repo, int skip, int count, Cancel cancel)
    {
        if (skip + count <= deepest &&
            Read(repo, skip, count, cancel) is { } commits)
        {
            return commits;
        }

        return GitHistory.Read(repo, skip, count, cancel);
    }

    /// <summary>
    /// The commits not pushed, as <see cref="GitHistory.Unpushed"/> finds them, in process.
    /// </summary>
    public static HashSet<string> UnpushedFaster(string repo, Cancel cancel) =>
        Unpushed(repo, cancel) ?? GitHistory.Unpushed(repo, cancel);

    /// <summary>
    /// The files a commit changed, as <see cref="GitHistory.FilesOf"/> lists them, in process.
    /// </summary>
    public static List<Change>? FilesOfFaster(string repo, GitCommit commit, Cancel cancel) =>
        FilesOf(repo, commit, cancel) ?? GitHistory.FilesOf(repo, commit, cancel);

    /// <summary>
    /// The commits from HEAD back, newest first, after the first <paramref name="skip"/>, as
    /// <see cref="GitHistory.Read(string, int, int, Cancel)"/> reads them. Empty before the first
    /// commit. Null when the repository can't be read, or not as git log would read it: with a
    /// .mailmap, which git follows for authors' names and LibGit2Sharp can't, or a commit message
    /// not in UTF-8, which git turns into UTF-8 and libgit2 doesn't.
    /// </summary>
    public static List<GitCommit>? Read(string repo, int skip, int count, Cancel cancel) =>
        Open(repo, cancel, _ => Read(_, skip, count), null);

    /// <summary>
    /// The commits on the branch that the branch it tracks hasn't got yet, as
    /// <see cref="GitHistory.Unpushed"/> finds them. None without a branch tracked. Null when the
    /// repository can't be read.
    /// </summary>
    public static HashSet<string>? Unpushed(string repo, Cancel cancel) =>
        Open(repo, cancel, Unpushed, null);

    /// <summary>
    /// The files <paramref name="commit"/> changed, against its first parent, as
    /// <see cref="GitHistory.FilesOf"/> lists them. Null when the repository can't be read.
    /// </summary>
    public static List<Change>? FilesOf(string repo, GitCommit commit, Cancel cancel) =>
        Open(repo, cancel, _ => FilesOf(_, commit.Sha), null);

    static T Open<T>(string repo, Cancel cancel, Func<Repository, T> read, T failed)
    {
        if (cancel.IsCancellationRequested)
        {
            return failed;
        }

        try
        {
            using var repository = new Repository(repo);
            return read(repository);
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
            return failed;
        }
    }

    // Newest first, as git log has them, with the tags on each.
    static List<GitCommit>? Read(Repository repository, int skip, int count)
    {
        // No commits yet, so nothing to log from.
        if (repository.Head.Tip is not { } tip)
        {
            return [];
        }

        if (HasMailmap(repository))
        {
            return null;
        }

        // Unsorted, which libgit2 walks in git log's own order, newest first, a commit at a time.
        // Sorted by time, it walks the whole history before giving the first.
        var tags = TagsByCommit(repository);
        var filter = new CommitFilter
        {
            IncludeReachableFrom = tip,
            SortBy = CommitSortStrategies.None
        };
        var read = repository.Commits
            .QueryBy(filter)
            .Skip(skip)
            .Take(count)
            .ToList();
        if (read.Any(_ => !IsUtf8(_.Encoding)))
        {
            return null;
        }

        return read
            .Select(_ => new GitCommit(
                _.Sha,
                _.Parents.Select(parent => parent.Sha).ToList(),
                _.Author.Name,
                DateTimeOffset.FromUnixTimeSeconds(_.Author.When.ToUnixTimeSeconds()),
                tags.GetValueOrDefault(_.Sha) ?? [],
                _.MessageShort))
            .ToList();
    }

    // Where git log would name authors as a .mailmap says: one at the top of the working tree,
    // or one the config names. A bare repository's is in HEAD's tree, which git reads too.
    static bool HasMailmap(Repository repository)
    {
        if (repository.Info.WorkingDirectory is not { } directory)
        {
            return true;
        }

        return File.Exists(Path.Combine(directory, ".mailmap")) ||
               repository.Config.Get<string>("mailmap.file") != null ||
               repository.Config.Get<string>("mailmap.blob") != null;
    }

    // As a commit's encoding header names it, UTF-8 when it has none.
    static bool IsUtf8(string? encoding) =>
        encoding == null ||
        encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("UTF8", StringComparison.OrdinalIgnoreCase);

    // The tags on each commit, as git log's %D lists them: an annotated tag on the commit it is
    // of. Tags of anything else, a tree say, are left out.
    static Dictionary<string, List<string>> TagsByCommit(Repository repository)
    {
        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tag in repository.Tags)
        {
            if (tag.PeeledTarget is not Commit commit)
            {
                continue;
            }

            if (!tags.TryGetValue(commit.Sha, out var names))
            {
                names = [];
                tags[commit.Sha] = names;
            }

            names.Add(tag.FriendlyName);
        }

        return tags;
    }

    static HashSet<string> Unpushed(Repository repository)
    {
        var head = repository.Head;
        if (head.Tip is not { } tip ||
            head.TrackedBranch?.Tip is not { } upstream)
        {
            return [];
        }

        var filter = new CommitFilter
        {
            IncludeReachableFrom = tip,
            ExcludeReachableFrom = upstream
        };
        return repository.Commits
            .QueryBy(filter)
            .Select(_ => _.Sha)
            .ToHashSet(StringComparer.Ordinal);
    }

    static List<Change>? FilesOf(Repository repository, string sha)
    {
        if (repository.Lookup<Commit>(sha) is not { } commit)
        {
            return null;
        }

        // Against nothing for the first commit, so everything in it is added, as diff-tree --root
        // has it.
        var parent = commit.Parents.FirstOrDefault();
        using var changes = repository.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree, compareOptions);
        var files = changes
            .Select(ChangeOf)
            .OfType<Change>()
            .ToList();
        files.Sort((first, second) => string.CompareOrdinal(first.Path, second.Path));
        return files;
    }

    // As GitHistory.ParseFiles reads diff-tree's letters: a copy is the file added, from where it
    // was copied, and a change of type is modified. Paths with git's slashes, which libgit2 gives
    // as Windows' backslashes.
    static Change? ChangeOf(TreeEntryChanges change)
    {
        var path = change.Path.Replace('\\', '/');
        var old = change.OldPath.Replace('\\', '/');
        return change.Status switch
        {
            ChangeKind.Added => new(path, null, GitChange.Added),
            ChangeKind.Deleted => new(path, null, GitChange.Deleted),
            ChangeKind.Renamed => new(path, old, GitChange.Renamed),
            ChangeKind.Copied => new(path, old, GitChange.Added),
            ChangeKind.Conflicted => new(path, null, GitChange.Conflicted),
            ChangeKind.Modified or ChangeKind.TypeChanged => new(path, null, GitChange.Modified),
            _ => null
        };
    }
}
