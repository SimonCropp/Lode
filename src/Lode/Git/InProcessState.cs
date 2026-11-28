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

            // Gone until there is something to count ahead and behind against, as git status
            // has it: the branch tracked there, and a commit checked out to count from.
            var state = new GitState(repository.Branch, repository.Head?.ToString(), 0, 0, files)
            {
                UpstreamName = upstreamRef == null ? null : ShortName(repository, upstreamRef),
                UpstreamGone = upstreamRef != null
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
                    Behind = behind.Count,
                    UpstreamGone = false
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
                Tag = Tag(repository, commits, headNode)
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
    /// or cancelled. Any of those on threads, as folders are listed ahead, which come wrapped.
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
            IndexOutOfRangeException ||
        (exception is AggregateException aggregate &&
         aggregate.InnerExceptions.All(IsUnreadable));

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

    // The forms git takes a short name in, as it reads refs/remotes/origin/main for origin/main,
    // in the order it tries them.
    static (string Prefix, string Suffix)[] shortForms =
    [
        ("", ""),
        ("refs/", ""),
        ("refs/tags/", ""),
        ("refs/heads/", ""),
        ("refs/remotes/", ""),
        ("refs/remotes/", "/HEAD")
    ];

    /// <summary>
    /// A ref's name as git status gives the branch tracked: as short as it can be while git would
    /// read it back as that ref and no other, refs/remotes/origin/main as origin/main, and
    /// refs/heads/main as main, but as remotes/origin/main where a branch here is called
    /// origin/main too.
    /// </summary>
    static string ShortName(GitRepository repository, string name)
    {
        // The last form first, the one that takes the most off. The first form, the name as it
        // is, always fits, so is the name given where no other will do.
        for (var form = shortForms.Length - 1; form > 0; form--)
        {
            var (prefix, suffix) = shortForms[form];
            if (name.Length <= prefix.Length + suffix.Length ||
                !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var shortName = name[prefix.Length..^suffix.Length];
            var ambiguous = false;
            for (var before = 0; before < form; before++)
            {
                var (beforePrefix, beforeSuffix) = shortForms[before];
                if (repository.Resolve(beforePrefix + shortName + beforeSuffix) != null)
                {
                    ambiguous = true;
                    break;
                }
            }

            if (!ambiguous)
            {
                return shortName;
            }
        }

        return name;
    }

    // The tag last found in each repository, by its folder, with what it was found from: the
    // commit checked out, where a shallow clone's history stops, and the tags' files as they
    // were before the tags were read. With what each tag whose ref doesn't say was of.
    sealed record Described(
        ObjectId Head,
        HashSet<ObjectId>? Shallow,
        List<(string Name, long Written, long Length)> TagFiles,
        string? Tag,
        Dictionary<ObjectId, ObjectId> Peeled);

    static Lock describing = new();
    static Dictionary<string, Described> described = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tag, as <see cref="Commits.Describe"/> finds it from <paramref name="head"/>, the
    /// commit checked out. As it was last found while that is still checked out, where a shallow
    /// clone's history stops is as it was, and the tags' files are as they were: every read that
    /// follows a save would otherwise walk the history again, all of it where no tag is reached,
    /// and without a commit graph, as a fresh clone has none until gc writes one, each commit's
    /// object inflated.
    /// </summary>
    internal static string? Tag(GitRepository repository, Commits commits, int head)
    {
        // Read before the tags are, so a tag changed meanwhile has them read again next time.
        var tagFiles = repository.TagFiles();
        var headId = commits.IdOf(head);
        var shallow = repository.Shallow;
        Described? last;
        lock (describing)
        {
            described.TryGetValue(repository.WorkTree, out last);
        }

        if (last != null &&
            last.Head == headId &&
            SameShallow(last.Shallow, shallow) &&
            last.TagFiles.SequenceEqual(tagFiles))
        {
            return last.Tag;
        }

        var peeled = new Dictionary<ObjectId, ObjectId>();
        var tag = commits.Describe(head, TagNames(repository, last?.Peeled, peeled));
        lock (describing)
        {
            described[repository.WorkTree] = new(headId, shallow, tagFiles, tag, peeled);
        }

        return tag;
    }

    static bool SameShallow(HashSet<ObjectId>? first, HashSet<ObjectId>? second)
    {
        if (first == null ||
            second == null)
        {
            return first == second;
        }

        return first.SetEquals(second);
    }

    /// <summary>
    /// The tags git describe --tags chooses from, by the commit each is of: of two on one
    /// commit, an annotated one over a light one, the one tagged later of two annotated, the first
    /// by name of two light. Each whose ref doesn't say what it is of, a loose one say, is peeled
    /// as <paramref name="peeledBefore"/> has it where it is there, as what an object is of never
    /// changes, and is given in <paramref name="peeled"/>.
    /// </summary>
    internal static Dictionary<ObjectId, (string Name, bool Annotated)> TagNames(
        GitRepository repository,
        IReadOnlyDictionary<ObjectId, ObjectId>? peeledBefore,
        Dictionary<ObjectId, ObjectId> peeled)
    {
        var names = new Dictionary<ObjectId, (string Name, bool Annotated, ObjectId Tag)>();
        foreach (var (name, id, packedPeel, peelKnown) in repository.Tags())
        {
            var target = id;
            if (packedPeel is { } known)
            {
                target = known;
            }
            else if (!peelKnown)
            {
                // Peeled before, by a read of the tags as they were, its object isn't read again.
                if (peeledBefore == null ||
                    !peeledBefore.TryGetValue(id, out target))
                {
                    target = repository.Peel(id).Id;
                }

                peeled[id] = target;
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
