/// <summary>
/// The going through folders that both searches do: the search box's, by name, and Find in
/// Files', of what files hold. Neither goes into a link to another folder, a junction or
/// symbolic link, which can lead round in a circle, nor what Show system files leaves out.
/// </summary>
static class SearchWalk
{
    /// <summary>
    /// Whether Show system files would leave an item out. That takes its full path, so is only
    /// asked of folders, and of what is marked system, which are all it could leave out.
    /// </summary>
    public static bool IsSystem(ref FileSystemEntry entry, SystemItems? hiddenSystem) =>
        hiddenSystem != null &&
        (entry.IsDirectory || (entry.Attributes & FileAttributes.System) != 0) &&
        hiddenSystem.IsSystem(entry.ToFullPath(), entry.Attributes);

    /// <summary>
    /// Whether a search goes into a folder: not one Show system files leaves out, nor a link to
    /// another folder, a junction or symbolic link, which can lead round in a circle, or to
    /// folders searched already. Other folders the file system marks the same way, as cloud
    /// storage can, are not links, and are gone into.
    /// </summary>
    public static bool CanSearch(ref FileSystemEntry entry, SystemItems? hiddenSystem)
    {
        if (IsSystem(ref entry, hiddenSystem))
        {
            return false;
        }

        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return true;
        }

        return IsNotLink(entry.ToFullPath());
    }

    static bool IsNotLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).LinkTarget == null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // What version control keeps for itself, never searched, as Sublime leaves them out.
    static string[] versionControl = [".git", ".hg", ".svn"];

    /// <summary>
    /// Whether a folder's name is what version control keeps for itself, .git say, which no
    /// search goes into.
    /// </summary>
    public static bool IsVersionControl(CharSpan name)
    {
        foreach (var kept in versionControl)
        {
            if (name.Equals(kept, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A file to search, and how big it is.
    /// </summary>
    public readonly record struct File(string Path, long Size);

    /// <summary>
    /// What a walk passed over that it would otherwise have searched, for the results to say.
    /// Added to from the walk's thread, read once it is done.
    /// </summary>
    public sealed class Report
    {
        /// <summary>
        /// The repositories git couldn't list, which were searched whole.
        /// </summary>
        public List<string> Unlisted { get; } = [];
    }

    // A folder still to go through, and what git doesn't ignore where it is: null for all of it.
    // A repository of its own, to be searched by what it doesn't ignore, is being listed.
    record struct Pending(string Path, string Root, GitScope? Scope, bool IsRepo);

    /// <summary>
    /// The files Find in Files searches, in order: each folder's files, by name as the list sorts
    /// them, then each of its subfolders the same way, all the way down. Hidden ones only when
    /// <see cref="FindQuery.Hidden"/>, and in a repository only what git doesn't ignore, unless
    /// <see cref="FindQuery.IncludeIgnored"/>.
    /// </summary>
    /// <param name="list">Lists what git doesn't ignore in a repository: by default
    /// <see cref="GitCache.Searchable"/>. Null when it can't.</param>
    public static IEnumerable<File> Files(
        SearchWhere where,
        FindQuery query,
        SystemItems? hiddenSystem,
        Func<string, GitScope?> list,
        Report report,
        Cancel cancel)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = default,
            ReturnSpecialDirectories = false
        };
        if (!query.Hidden)
        {
            options.AttributesToSkip = FileAttributes.Hidden;
        }

        // Each repository is listed as soon as the walk comes to it, several at once, rather than
        // when it is gone into: listing starts git, which takes as long as listing a folder of
        // repositories does, and the walk would wait on each in turn.
        var listing = new Dictionary<string, Task<GitScope?>>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var listers = new Listers(list, cancel);

        void StartListing(string top)
        {
            if (!listing.ContainsKey(top))
            {
                listing[top] = listers.List(top);
            }
        }

        GitScope? ScopeOf(string top)
        {
            StartListing(top);
            var scope = listing[top].GetAwaiter().GetResult();
            if (scope == null &&
                reported.Add(top))
            {
                report.Unlisted.Add(top);
            }

            return scope;
        }

        foreach (var root in where.Roots)
        {
            cancel.ThrowIfCancellationRequested();

            // A file given by name is searched, whatever would leave it out.
            if (System.IO.File.Exists(root))
            {
                yield return new(root, SizeOf(root));
                continue;
            }

            GitScope? rootScope = null;
            if (!query.IncludeIgnored &&
                RepoAbove(root) is { } top)
            {
                rootScope = ScopeOf(top);
            }

            var stack = new Stack<Pending>();
            stack.Push(new(root, root, rootScope, false));
            while (stack.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var (folder, from, scope, isRepo) = stack.Pop();
                if (isRepo)
                {
                    scope = ScopeOf(folder);
                }

                var (files, folders) = Read(folder, options, hiddenSystem);

                foreach (var file in files)
                {
                    var relative = Relative(from, file.Path);
                    if (where.IncludesFile(relative, file.Name) &&
                        (scope == null || scope.HasFile(file.Path)))
                    {
                        yield return new(file.Path, file.Size);
                    }
                }

                for (var index = folders.Count - 1; index >= 0; index--)
                {
                    var subfolder = folders[index];
                    if (versionControl.Contains(subfolder.Name, StringComparer.OrdinalIgnoreCase) ||
                        !where.IncludesFolder(Relative(from, subfolder.Path), subfolder.Name) ||
                        (scope != null && !scope.HasFolder(subfolder.Path)))
                    {
                        continue;
                    }

                    // A repository of its own, nested in another or not in one at all, is searched
                    // by what it doesn't ignore. Asked of the folder, not found among what it
                    // holds, as git hides .git, which isn't listed when hidden files aren't.
                    var repo = !query.IncludeIgnored &&
                               IsRepo(subfolder.Path);
                    if (repo)
                    {
                        StartListing(subfolder.Path);
                    }

                    stack.Push(new(subfolder.Path, from, scope, repo));
                }
            }
        }
    }

    // Lists repositories several at once, each on a thread of its own, as each waits on git:
    // started the first time one is asked for. Let go of, those not yet listed never are.
    sealed class Listers(Func<string, GitScope?> list, Cancel cancel) : IDisposable
    {
        BlockingCollection<(string Top, TaskCompletionSource<GitScope?> Done)> waiting = new();
        bool started;
        volatile bool finished;

        public Task<GitScope?> List(string top)
        {
            if (!started)
            {
                started = true;
                for (var index = 0; index < Math.Clamp(Environment.ProcessorCount, 2, 8); index++)
                {
                    ContentSearch.OwnThread(Run);
                }
            }

            var done = new TaskCompletionSource<GitScope?>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Add((top, done), cancel);
            return done.Task;
        }

        void Run()
        {
            foreach (var (top, done) in waiting.GetConsumingEnumerable())
            {
                if (finished ||
                    cancel.IsCancellationRequested)
                {
                    done.TrySetCanceled();
                    continue;
                }

                try
                {
                    done.TrySetResult(list(top));
                }
                catch (Exception exception)
                {
                    done.TrySetException(exception);
                }
            }
        }

        public void Dispose()
        {
            finished = true;
            waiting.CompleteAdding();
        }
    }

    // A file or folder as a folder lists it.
    readonly record struct Item(string Name, string Path, long Size);

    // What a folder holds, files and subfolders apart, each by name. Nothing for one that went,
    // or can't be read.
    static (List<Item> Files, List<Item> Folders) Read(string folder, EnumerationOptions options, SystemItems? hiddenSystem)
    {
        var files = new List<Item>();
        var folders = new List<Item>();
        try
        {
            var items = new FileSystemEnumerable<(Item Item, bool IsDirectory)>(
                folder,
                (ref entry) => (new(entry.FileName.ToString(), entry.ToFullPath(), entry.Length), entry.IsDirectory),
                options)
            {
                ShouldIncludePredicate = (ref entry) =>
                {
                    if (entry.IsDirectory)
                    {
                        return CanSearch(ref entry, hiddenSystem);
                    }

                    return !IsSystem(ref entry, hiddenSystem);
                }
            };
            foreach (var (item, isDirectory) in items)
            {
                if (isDirectory)
                {
                    folders.Add(item);
                }
                else
                {
                    files.Add(item);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Gone while it was searched, or its drive went.
        }

        files.Sort(static (first, second) => StrCmpLogicalW(first.Name, second.Name));
        folders.Sort(static (first, second) => StrCmpLogicalW(first.Name, second.Name));
        return (files, folders);
    }

    static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // Where a path is from the folder searched.
    static string Relative(string from, string path)
    {
        if (path.Length > from.Length &&
            path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
        {
            var start = from.Length;
            if (path[start] == '\\')
            {
                start++;
            }

            return path[start..];
        }

        return path;
    }

    /// <summary>
    /// The repository a folder is in, however far up: a folder holding a .git folder, or the
    /// .git file of a worktree or submodule. Null outside any.
    /// </summary>
    public static string? RepoAbove(string folder)
    {
        for (var current = folder; current != null; current = Path.GetDirectoryName(current))
        {
            if (IsRepo(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// A .git folder, or the .git file of a worktree or submodule, asked of the file system once
    /// for either, as it is asked of every folder the walk comes to.
    /// </summary>
    public static bool IsRepo(string folder) =>
        Path.Exists(Path.Combine(folder, ".git"));
}
