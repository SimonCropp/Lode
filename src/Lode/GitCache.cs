/// <summary>
/// What was last read of each repository's git state, kept until something in it changes, as
/// TortoiseGit's cache keeps it: a folder listing repositories, gone into again, shows them as
/// they were without reading them again. The drive the code folder is on is watched, everything
/// on it, from its root, which no folder needs letting go of to be moved, renamed or deleted. A
/// change in a repository, to its files or in .git, makes it stale, and once its changes have
/// stopped for a moment the views showing it read it again. Where the drive can't be watched,
/// a share say, nothing is kept fresh, so each is read every time, as before.
/// What git writes for itself, that no state shows, is no change: see <see cref="IsNoise"/>.
/// And a repository whose changes go on, a build's output say, while reading it finds nothing
/// new, is read less and less often until they stop: see <see cref="Known"/>.
/// </summary>
static class GitCache
{
    /// <summary>
    /// What is known of one repository, and when it is next to be read. Times are in
    /// milliseconds, as Environment.TickCount64 counts them, and are given, so what it does as
    /// time passes can be tested without waiting. GitCache uses each under its gate.
    /// </summary>
    internal sealed class Known(int changes = 0)
    {
        public GitState? State { get; private set; }
        public RemoteSite? Site { get; private set; }

        /// <summary>
        /// How many changes have been seen in it.
        /// </summary>
        public int Changes { get; private set; } = changes;

        // How many had been when what is kept was read: fresh while they are the same.
        int readAt = -1;

        // When the changes its views are yet to be told of began, and when the last one came.
        long since;
        long last;

        // How long it is held for, not read, after a read that found nothing new, and until when
        // it is. Twice as long each time, as then its changes are ones no state shows, a build's
        // output say, and each read is of every file in it.
        long hold;
        long heldUntil;

        /// <summary>
        /// Whether its views are yet to be told of changes in it.
        /// </summary>
        public bool Changing { get; private set; }

        /// <summary>
        /// Whether what is kept needs no reading: nothing has changed since it was read, or it
        /// is held.
        /// </summary>
        public bool Fresh(long now) =>
            State != null &&
            (Changes == readAt || now < HeldUntil);

        // Held only while its changes go on. Once they have stopped a while it is read, however
        // much longer it was held for, so what it comes to rest as is soon shown.
        long HeldUntil =>
            Math.Min(heldUntil, last + longestSettle);

        /// <summary>
        /// When its views are to be told of its changes: once they have stopped for a moment, or
        /// have gone on a while, but not while it is held.
        /// </summary>
        public long Due =>
            Math.Max(
                Math.Min(last + (long) Settle.TotalMilliseconds, since + longestSettle),
                HeldUntil);

        /// <summary>
        /// A change in it. <paramref name="inGit"/> for one in .git, other than
        /// <see cref="IsNoise"/>: a commit, a stage or a pull, which are what a state is made
        /// of, so it is held no longer.
        /// </summary>
        public void Changed(long now, bool inGit)
        {
            Changes++;

            // Stopped a while, its changes begin afresh: these may be nothing like the last.
            var begun = now - last >= longestSettle;
            if (begun)
            {
                hold = 0;
            }

            // Held no longer, what changed while it was is read with this, once this settles.
            var freed = heldUntil != 0 &&
                        (begun || inGit);
            if (freed)
            {
                heldUntil = 0;
            }

            if (begun ||
                freed ||
                !Changing)
            {
                since = now;
            }

            Changing = true;
            last = now;
        }

        /// <summary>
        /// Its views have been told of its changes.
        /// </summary>
        public void Told() =>
            Changing = false;

        /// <summary>
        /// To be read whatever has been seen of it: Refresh, or anything may have changed.
        /// </summary>
        public void Ask()
        {
            Changes++;
            hold = 0;
            heldUntil = 0;
        }

        /// <summary>
        /// What a read of it found, begun when <paramref name="changesBefore"/> changes had been
        /// seen.
        /// </summary>
        public void Read(GitState state, RemoteSite? site, int changesBefore, long now)
        {
            // Only a read of changes not read before says anything of them: not a second view
            // reading the same, nor the one read told of twice.
            if (changesBefore != readAt)
            {
                if (State == state &&
                    Site == site)
                {
                    hold = Math.Clamp(hold * 2, longestSettle, longestHold);
                    heldUntil = now + hold;
                }
                else
                {
                    hold = 0;
                    heldUntil = 0;
                }
            }

            State = state;
            Site = site;
            readAt = changesBefore;
        }
    }

    static readonly Lock gate = new();
    static readonly Dictionary<string, Known> known = new(StringComparer.OrdinalIgnoreCase);
    static FolderWatcher? watcher;
    static string? watchedRoot;
    // Every code folder watched for, as Options may change it, and tests have many.
    static readonly HashSet<string> codeFolders = new(StringComparer.OrdinalIgnoreCase);

    // The repositories changing, until the views are told of each: see Known.Due. The timer is
    // set for the first of them due, and when that is, which is never while none is.
    static readonly Dictionary<string, Known> changing = new(StringComparer.OrdinalIgnoreCase);
    static System.Threading.Timer? settling;
    static long settlingAt = long.MaxValue;

    /// <summary>
    /// How long a repository's changes must stop for before it is read again: a commit, a pull
    /// or a build changes many files, one after another.
    /// </summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(500);

    // Nor longer than this, however long the changes go on, a build say. In milliseconds.
    const long longestSettle = 3_000;

    // The longest a repository is held for, its changes going on and nothing new found.
    const long longestHold = 60_000;

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
    /// the repository has changed since, as far as the watch has heard, or what has is its
    /// output still being built. False when none has been.
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
            fresh = entry.Fresh(Environment.TickCount64) && watcher is { IsWatching: true };
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
                entry = new(changesBefore);
                known[repo] = entry;
            }

            entry.Read(state, site, changesBefore, Environment.TickCount64);
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
                    entry.Ask();
                }
            }
        }
    }

    const string gitFolder = @"\.git\";

    /// <summary>
    /// Whether a change at <paramref name="path"/> is one git makes for itself, which no state
    /// shows, and so is no reason to read the repository again. Objects, which count only once a
    /// branch or the index names them, and that is a change of its own. The log of where each
    /// branch has been. A lock, taken to write the file it is beside. And what git's fsmonitor
    /// daemon makes and deletes for every status it answers, Lode's own among them: counted,
    /// each read of such a repository would be the reason for the next.
    /// </summary>
    internal static bool IsNoise(CharSpan path) =>
        InGit(path, out var inside) &&
        IsNoiseInGit(inside);

    static bool IsNoiseInGit(CharSpan inside) =>
        Locations.IsWithin(inside, "objects") ||
        Locations.IsWithin(inside, "logs") ||
        Locations.IsWithin(inside, "fsmonitor--daemon") ||
        inside.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    // Whether the path is in a .git folder, and where in it. A worktree's and a submodule's own
    // are in the .git of the repository they are of.
    static bool InGit(CharSpan path, out CharSpan inside)
    {
        var at = path.IndexOf(gitFolder, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            inside = default;
            return false;
        }

        inside = path[(at + gitFolder.Length)..];
        return true;
    }

    // On a background thread, for everything changed on the drive, so as little as it can be for
    // what is outside the code folder. Stale at once, so a view going to it reads it; the views
    // showing it are told once its changes stop.
    internal static void ChangedAt(string path)
    {
        lock (gate)
        {
            var codeFolder = codeFolders.FirstOrDefault(_ => Locations.IsWithin(path, _));
            if (codeFolder == null)
            {
                return;
            }

            var inGit = InGit(path, out var inside);
            if (inGit &&
                IsNoiseInGit(inside))
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

                var now = Environment.TickCount64;
                entry.Changed(now, inGit);
                changing[current] = entry;

                // Left as it is set when that is soon enough, as it nearly always is: it then
                // finds this one not yet due, and waits on.
                if (entry.Due < settlingAt)
                {
                    SettleAt(entry.Due, now);
                }

                return;
            }
        }
    }

    // Under the gate.
    static void SettleAt(long due, long now)
    {
        settlingAt = due;
        settling ??= new(_ => Settled());
        settling.Change(Math.Max(due - now, 0), Timeout.Infinite);
    }

    // The timer's: tells of the repositories due, and waits for the next of the rest.
    static void Settled()
    {
        var stale = new List<string>();
        lock (gate)
        {
            var now = Environment.TickCount64;
            var next = long.MaxValue;
            foreach (var (repo, entry) in changing)
            {
                if (entry.Due > now)
                {
                    next = Math.Min(next, entry.Due);
                    continue;
                }

                entry.Told();
                stale.Add(repo);
            }

            foreach (var repo in stale)
            {
                changing.Remove(repo);
            }

            settlingAt = long.MaxValue;
            if (next != long.MaxValue)
            {
                SettleAt(next, now);
            }
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
            entry.Ask();
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
