/// <summary>
/// Reads a repository's state in Lode's own process, from the files git keeps, rather than by
/// running git: the same branch, changes, ahead and behind, how far behind upstream a fork is,
/// and tag as <see cref="GitState.FromGit"/> finds. Where the repository has what isn't read
/// here as git would read it, a filter only git can run say, it is left to git.
/// </summary>
static class InProcessState
{
    /// <summary>
    /// Null where the repository can't be read here. None of its files stay open once this
    /// returns, as Windows would then not let them, or the folder, be deleted.
    /// </summary>
    public static GitState? FromFiles(string repo, Cancel cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            using var repository = GitRepository.Open(repo);
            if (repository == null)
            {
                return null;
            }

            var files = WorkingTree.Read(repository, cancel);
            var upstreamRef = UpstreamRef(repository);
            var state = new GitState(repository.Branch, repository.Head?.ToString(), 0, 0, files)
            {
                Upstream = upstreamRef != null
            };
            if (repository.Head is not { } head)
            {
                return state;
            }

            var commits = new Commits(repository);
            var headNode = commits.Node(head);
            if (upstreamRef != null &&
                repository.Resolve(upstreamRef) is { } upstream)
            {
                var (ahead, behind) = commits.Divergence(headNode, commits.Node(upstream));
                commits.ClearFlags();
                state = state with
                {
                    Ahead = ahead.Count,
                    Behind = behind.Count
                };
            }

            if (repository.Resolve("refs/remotes/upstream/HEAD") is { } fork)
            {
                var (_, forkBehind) = commits.Divergence(headNode, commits.Node(fork));
                commits.ClearFlags();
                state = state with
                {
                    ForkBehind = forkBehind.Count
                };
            }

            return state with
            {
                Tag = commits.Describe(headNode, TagNames(repository))
            };
        }
        catch (Exception exception)
            when (IsUnreadable(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Why a repository couldn't be read here: what isn't read as git would, a file gone part way,
    /// or cancelled.
    /// </summary>
    internal static bool IsUnreadable(Exception exception) =>
        exception is
            GitFilesException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            OperationCanceledException or
            ArgumentException or
            FormatException or
            OverflowException or
            IndexOutOfRangeException;

    // As git status says # branch.upstream: the branch tracks one a refspec fetches, whether it
    // is there or has gone. None detached.
    static string? UpstreamRef(GitRepository repository)
    {
        if (repository.Branch is not { } branch)
        {
            return null;
        }

        return repository.Upstream(branch);
    }

    /// <summary>
    /// The tags git describe --tags chooses from, by the commit each is of: of two on one
    /// commit, an annotated one over a light one, the one tagged later of two annotated, the first
    /// by name of two light.
    /// </summary>
    internal static Dictionary<ObjectId, (string Name, bool Annotated)> TagNames(GitRepository repository)
    {
        var names = new Dictionary<ObjectId, (string Name, bool Annotated, ObjectId Tag)>();
        foreach (var (name, id, peeled, peelKnown) in repository.Tags())
        {
            var target = id;
            if (peeled is { } known)
            {
                target = known;
            }
            else if (!peelKnown)
            {
                target = repository.Peel(id).Id;
            }

            var annotated = target != id;
            var shortName = name["refs/tags/".Length..];
            if (!names.TryGetValue(target, out var existing) ||
                (annotated && !existing.Annotated) ||
                (annotated && existing.Annotated && TaggedBefore(repository, existing.Tag, id)))
            {
                names[target] = (shortName, annotated, id);
            }
        }

        return names.ToDictionary(_ => _.Key, _ => (_.Value.Name, _.Value.Annotated));
    }

    static bool TaggedBefore(GitRepository repository, ObjectId first, ObjectId second)
    {
        var firstDate = GitRepository.TagTarget(repository.Objects.Read(first, GitObjectType.Tag)).Date;
        var secondDate = GitRepository.TagTarget(repository.Objects.Read(second, GitObjectType.Tag)).Date;
        return firstDate < secondDate;
    }

    /// <summary>
    /// The repository read in process, however many files it has: listing a folder at a time
    /// beats git's look at every file, as well as saving git starting (GitStateBenchmarks). Git
    /// for one that can't be read here, of a format newer than is known say.
    /// </summary>
    public static GitState? FromFaster(string repo, Cancel cancel) =>
        FromFiles(repo, cancel) ?? GitState.FromGit(repo, cancel);

    /// <summary>
    /// Which of <paramref name="folders"/>, in the repository, git ignores, and of the folders
    /// each is in, down from the repository, as far as the first that it does: everything in
    /// that one is ignored with it. A folder ignored that holds a file git tracks all the same,
    /// one added by force say, is not given as ignored, as a change to that file is a change.
    /// Each by its path. All as not ignored where the repository can't be read here.
    /// </summary>
    public static Dictionary<string, bool> Ignored(string repo, IReadOnlyCollection<string> folders)
    {
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var repository = GitRepository.Open(repo);
            if (repository == null)
            {
                return AllNotIgnored(found, folders);
            }

            var rules = new IgnoreRules(repository, repo);
            var entered = new Dictionary<string, IgnoreRules.Folder>(StringComparer.OrdinalIgnoreCase)
            {
                [repo] = rules.Top()
            };
            foreach (var folder in folders)
            {
                var current = repo;
                var parent = entered[repo];
                foreach (var part in Path.GetRelativePath(repo, folder).Split(Path.DirectorySeparatorChar))
                {
                    current = Path.Combine(current, part);
                    if (!entered.TryGetValue(current, out var child))
                    {
                        child = rules.Enter(parent, part);
                        entered[current] = child;
                    }

                    found[current] = child.Ignored;
                    if (child.Ignored)
                    {
                        break;
                    }

                    parent = child;
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

            var index = IndexFile.Read(repository.GitDir);
            for (var entry = 0; entry < index.Entries.Length; entry++)
            {
                var path = index.PathText(entry);
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
            when (IsUnreadable(exception))
        {
            return AllNotIgnored(found, folders);
        }

        return found;
    }

    static Dictionary<string, bool> AllNotIgnored(Dictionary<string, bool> found, IReadOnlyCollection<string> folders)
    {
        foreach (var folder in folders)
        {
            found[folder] = false;
        }

        return found;
    }

    // As git has a folder's path: from the top of the repository, with forward slashes, and
    // one after it.
    static string Relative(string repo, string folder) =>
        Path.GetRelativePath(repo, folder).Replace('\\', '/') + '/';
}
