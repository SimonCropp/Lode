/// <summary>
/// What was last read of each repository's git state, kept until something in it changes, as
/// TortoiseGit's cache keeps it: a folder listing repositories, gone into again, shows them as
/// they were without reading them again. The drive the code folder is on is watched, everything
/// on it, from its root, which no folder needs letting go of to be moved, renamed or deleted. A
/// change in a repository, to its files or in .git, makes it stale, and once its changes have
/// stopped for a moment the views showing it read it again. Where the drive can't be watched,
/// a share say, nothing is kept fresh, so each is read every time, as before.
/// </summary>
static class GitCache
{
    sealed class Known
    {
        public GitState? State;
        public RemoteSite? Site;

        // Changes seen in it, and how many had been when what is kept was read: fresh while
        // they are the same.
        public int Changes;
        public int ReadAt = -1;

        public bool Fresh => State != null && Changes == ReadAt;
    }

    static readonly Lock gate = new();
    static readonly Dictionary<string, Known> known = new(StringComparer.OrdinalIgnoreCase);
    static FolderWatcher? watcher;
    static string? watchedRoot;
    // Every code folder watched for, as Options may change it, and tests have many.
    static readonly HashSet<string> codeFolders = new(StringComparer.OrdinalIgnoreCase);

    // The repositories changing, until their changes stop for a moment.
    static readonly HashSet<string> changing = new(StringComparer.OrdinalIgnoreCase);
    static System.Threading.Timer? settling;
    static long changingSince;

    /// <summary>
    /// How long a repository's changes must stop for before it is read again: a commit, a pull
    /// or a build changes many files, one after another.
    /// </summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(500);

    // Nor longer than this, however long the changes go on, a build say.
    static readonly TimeSpan longestSettle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Raised on a background thread with the repositories that have gone stale, for the views
    /// showing them to read them again, each on its own thread.
    /// </summary>
    public static event Action<IReadOnlyCollection<string>>? Stale;

    /// <summary>
    /// Whether changes are being heard, so what is cached stays fresh until one comes.
    /// </summary>
    public static bool Watching
    {
        get
        {
            lock (gate)
            {
                return watcher is { IsWatching: true };
            }
        }
    }

    /// <summary>
    /// Watches the drive <paramref name="folder"/>, the code folder, is on, unless it is watched
    /// already.
    /// </summary>
    public static void Watch(string folder)
    {
        var root = Path.GetPathRoot(folder);
        lock (gate)
        {
            codeFolders.Add(folder);
            if (string.Equals(root, watchedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            watcher?.Dispose();
            watcher = null;
            watchedRoot = root;
            MarkAllStale();
            if (root is null or "" ||
                Locations.IsRemote(root))
            {
                return;
            }
        }

        FolderWatcher? started = null;
        try
        {
            started = new(
                root,
                NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                AllChanged,
                subfolders: true,
                changedAt: ChangedAt);
        }
        catch (Exception)
        {
            // Not watched, so each repository is read every time.
        }

        lock (gate)
        {
            if (watcher == null &&
                string.Equals(root, watchedRoot, StringComparison.OrdinalIgnoreCase))
            {
                watcher = started;
                return;
            }
        }

        started?.Dispose();
    }

    /// <summary>
    /// The state last read of <paramref name="repo"/>, and whether it is still fresh: nothing in
    /// the repository has changed since, as far as the watch has heard. False when none has been.
    /// </summary>
    public static bool TryGet(string repo, out GitState state, out RemoteSite? site, out bool fresh)
    {
        lock (gate)
        {
            if (!known.TryGetValue(repo, out var entry) ||
                entry.State == null)
            {
                state = null!;
                site = null;
                fresh = false;
                return false;
            }

            state = entry.State;
            site = entry.Site;
            fresh = entry.Fresh && watcher is { IsWatching: true };
            return true;
        }
    }

    /// <summary>
    /// How many changes have been seen in <paramref name="repo"/>, asked as a read of it starts,
    /// for <see cref="Store"/> to tell whether one came while it was read. Its changes are heard
    /// from then on.
    /// </summary>
    public static int Changes(string repo)
    {
        lock (gate)
        {
            if (!known.TryGetValue(repo, out var entry))
            {
                entry = new();
                known[repo] = entry;
            }

            return entry.Changes;
        }
    }

    /// <summary>
    /// What a read of <paramref name="repo"/> found, which is fresh unless something changed in it
    /// while it was read. Null for one that isn't a repository git will take.
    /// </summary>
    public static void Store(string repo, GitState? state, RemoteSite? site, int changesBefore)
    {
        lock (gate)
        {
            if (state == null)
            {
                known.Remove(repo);
                return;
            }

            if (!known.TryGetValue(repo, out var entry))
            {
                entry = new()
                {
                    Changes = changesBefore
                };
                known[repo] = entry;
            }

            entry.State = state;
            entry.Site = site;
            entry.ReadAt = changesBefore;
        }
    }

    /// <summary>
    /// Makes the repositories stale, for Refresh, which reads them again whatever the watch says.
    /// </summary>
    public static void MarkStale(IEnumerable<string> repos)
    {
        lock (gate)
        {
            foreach (var repo in repos)
            {
                if (known.TryGetValue(repo, out var entry))
                {
                    entry.Changes++;
                }
            }
        }
    }

    // On a background thread, for everything changed on the drive, so as little as it can be for
    // what is outside the code folder. Stale at once, so a view going to it reads it; the views
    // showing it are told once its changes stop.
    static void ChangedAt(string path)
    {
        lock (gate)
        {
            var codeFolder = codeFolders.FirstOrDefault(_ => Locations.IsWithin(path, _));
            if (codeFolder == null)
            {
                return;
            }

            // The innermost repository it is in: one nested in another is its own.
            for (var current = path; current != null && Locations.IsWithin(current, codeFolder); current = Path.GetDirectoryName(current))
            {
                if (!known.TryGetValue(current, out var entry))
                {
                    continue;
                }

                entry.Changes++;
                if (changing.Add(current) &&
                    changing.Count == 1)
                {
                    changingSince = Stopwatch.GetTimestamp();
                }

                // Read once its changes stop, or have gone on a while.
                var wait = Settle;
                var waited = Stopwatch.GetElapsedTime(changingSince);
                if (waited + wait > longestSettle)
                {
                    wait = longestSettle - waited;
                    if (wait < TimeSpan.Zero)
                    {
                        wait = TimeSpan.Zero;
                    }
                }

                settling ??= new(_ => Settled());
                settling.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }
    }

    static void Settled()
    {
        List<string> stale;
        lock (gate)
        {
            stale = changing.ToList();
            changing.Clear();
        }

        Raise(stale);
    }

    // Too many changes at once to keep track of, or a watch that failed or was let go of and is
    // back: anything may have changed.
    static void AllChanged()
    {
        List<string> stale;
        lock (gate)
        {
            MarkAllStale();
            stale = known.Keys.ToList();
        }

        Raise(stale);
    }

    // Under the gate.
    static void MarkAllStale()
    {
        foreach (var entry in known.Values)
        {
            entry.Changes++;
        }
    }

    static void Raise(List<string> stale)
    {
        if (stale.Count == 0)
        {
            return;
        }

        Stale?.Invoke(stale);
    }

    /// <summary>
    /// Forgets every repository and stops watching, for tests.
    /// </summary>
    internal static void Reset()
    {
        lock (gate)
        {
            known.Clear();
            changing.Clear();
            watcher?.Dispose();
            watcher = null;
            watchedRoot = null;
            codeFolders.Clear();
        }
    }
}
