/// <summary>
/// What was last read of each repository's git state, kept until something in it changes, as
/// TortoiseGit's cache keeps it: a folder listing repositories, gone into again, shows them as
/// they were without reading them again. The code folder is watched, everything in it, and
/// nothing else on its drive is heard of. While it is, Windows lets nobody rename or move a
/// folder the code folder is in, so it is let go of once it is the code folder no longer: see
/// <see cref="LetGo"/>. A change in a repository, to its files or in .git, makes it stale, and
/// once its changes have stopped for a moment the views showing it read it again. Where the
/// folder can't be watched, on a share say, or not there, nothing is kept fresh, so each is read
/// every time, as before.
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
        /// The folders in it that have been looked at for whether git ignores them, and how
        /// many times what was learned of them has been forgotten: see <see cref="folders"/>.
        /// </summary>
        public List<string> Learned { get; } = [];

        public int Forgotten { get; set; }

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
        /// Anything may have changed in it, the watch having lost track: its views are told as
        /// they are of a change of git's own, once that has settled, and it is held no more.
        /// </summary>
        public void AnyChanged(long now)
        {
            hold = 0;
            Changed(now, inGit: true);
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

    static Lock gate = new();
    static Dictionary<string, Known> known = new(StringComparer.OrdinalIgnoreCase);

    // The git folder of each worktree and submodule known, which is in the .git of another
    // repository, with whether it shares that one's branches, as a worktree does. And for each
    // git folder shared, the worktrees sharing it.
    static Dictionary<string, (string Repo, bool Shares)> gitFolders = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, List<string>> sharing = new(StringComparer.OrdinalIgnoreCase);

    // The watch on each code folder: one, but for tests, which have many. Null for one that
    // can't be watched, on a share say, or not there when it was first shown.
    static Dictionary<string, FolderWatcher?> watchers = new(StringComparer.OrdinalIgnoreCase);

    // Whether a watch that failed, its folder deleted say, is being started again.
    static bool reviving;

    // The repositories changing, until the views are told of each: see Known.Due. The timer is
    // set for the first of them due, and when that is, which is never while none is.
    static Dictionary<string, Known> changing = new(StringComparer.OrdinalIgnoreCase);
    static System.Threading.Timer? settling;
    static long settlingAt = long.MaxValue;

    /// <summary>
    /// How long a repository's changes must stop for before it is read again: a commit, a pull
    /// or a build changes many files, one after another, and an editor or an agent saves one
    /// file after another, a moment apart.
    /// </summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(1);

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
    /// Whether changes in <paramref name="folder"/>, the code folder, are being heard, so what
    /// is cached of its repositories stays fresh until one comes.
    /// </summary>
    public static bool IsWatching(string? folder)
    {
        if (folder == null)
        {
            return false;
        }

        lock (gate)
        {
            return watchers.GetValueOrDefault(folder) is
            {
                IsWatching: true
            };
        }
    }

    // Whether changes to what is at the path are being heard. Under the gate.
    static bool Heard(CharSpan path)
    {
        foreach (var (folder, watcher) in watchers)
        {
            if (Locations.IsWithin(path, folder))
            {
                return watcher is
                {
                    IsWatching: true
                };
            }
        }

        return false;
    }

    /// <summary>
    /// Watches <paramref name="folder"/>, the code folder, unless it is watched already. One
    /// whose watch failed, the folder deleted and made again say, is watched again.
    /// </summary>
    public static void Watch(string folder)
    {
        lock (gate)
        {
            if (watchers.TryGetValue(folder, out var existing))
            {
                // Starting a watch can wait on the drive, and this is a window's thread.
                if (existing is
                    {
                        IsWatching: false
                    } &&
                    !reviving)
                {
                    reviving = true;
                    Task.Run(() => Revive(folder, existing));
                }

                return;
            }

            watchers[folder] = null;
            MarkStaleIn(folder);
            var root = Path.GetPathRoot(folder);
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
                folder,
                NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                () => AllChanged(folder),
                subfolders: true,
                changedAt: ChangedAt);
        }
        catch (Exception)
        {
            // Not watched, so each repository is read every time.
        }

        lock (gate)
        {
            if (watchers.TryGetValue(folder, out var existing) &&
                existing == null)
            {
                watchers[folder] = started;
                return;
            }
        }

        started?.Dispose();
    }

    // Anything may have changed while it wasn't watched, and what was read then would count as
    // fresh from here on.
    static void Revive(string folder, FolderWatcher failed)
    {
        failed.Revive();
        lock (gate)
        {
            reviving = false;
            if (failed.IsWatching)
            {
                MarkStaleIn(folder);
            }
        }
    }

    /// <summary>
    /// Stops watching <paramref name="folder"/>, which is the code folder no longer: Options
    /// has another, or none. Nothing of Lode's then stops a folder it is in being renamed or
    /// moved.
    /// </summary>
    public static void LetGo(string folder)
    {
        FolderWatcher? watcher;
        lock (gate)
        {
            if (!watchers.Remove(folder, out watcher))
            {
                return;
            }

            MarkStaleIn(folder);
        }

        watcher?.Dispose();
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
            fresh = entry.Fresh(Environment.TickCount64) &&
                    Heard(repo);
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
            if (known.TryGetValue(repo, out var entry))
            {
                return entry.Changes;
            }
        }

        // Read outside the gate, which every change on the drive waits on.
        var own = OwnGitFolder(repo);
        lock (gate)
        {
            if (!known.TryGetValue(repo, out var entry))
            {
                entry = new();
                known[repo] = entry;
                Follow(repo, own);
            }

            return entry.Changes;
        }
    }

    // Where the git folder of a worktree or a submodule is, which is not in it but in the .git
    // of the repository it is of, as its .git file says, and the folder whose branches and
    // config it shares, when it shares one: a worktree does, a submodule has its own. Null for
    // a repository with a .git folder of its own, or one whose .git can't be read.
    static (string Git, string Common)? OwnGitFolder(string repo)
    {
        try
        {
            if (Directory.Exists(Path.Combine(repo, ".git")) ||
                CodeCommands.GitDirectory(repo) is not { } git)
            {
                return null;
            }

            return (git, CodeCommands.CommonDirectory(git));
        }
        catch (Exception exception)
            when (exception is
                      IOException or
                      UnauthorizedAccessException or
                      ArgumentException)
        {
            return null;
        }
    }

    // Under the gate.
    static void Follow(string repo, (string Git, string Common)? own)
    {
        if (own is not var (git, common))
        {
            return;
        }

        var shares = !Locations.Same(git, common);
        gitFolders[git] = (repo, shares);
        if (!shares)
        {
            return;
        }

        if (!sharing.TryGetValue(common, out var repos))
        {
            repos = [];
            sharing[common] = repos;
        }

        if (!repos.Contains(repo, StringComparer.OrdinalIgnoreCase))
        {
            repos.Add(repo);
        }
    }

    // A read of a repository under way, begun when so many changes had been seen in it, for
    // whoever asked. Done once it has been read.
    sealed class Reading(int changes, Cancel cancel)
    {
        public int Changes => changes;
        public Cancel Cancel => cancel;
        public TaskCompletionSource<(GitState? State, RemoteSite? Site)> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The folders looked at for whether git ignores them, with whether it does, and so whether
    // a change in one is no change to its repository: a build's output, nearly always. Learned
    // of a folder the first time something changes in it, off this thread, as it is asked of
    // the repository. Forgotten, for a repository, when what git ignores in it may be other
    // than it was: see Forget.
    static Dictionary<string, bool> folders = new(StringComparer.OrdinalIgnoreCase);

    // The folders yet to be looked at, each with its repository, and whether they are being.
    static Dictionary<string, string> toLearn = new(StringComparer.OrdinalIgnoreCase);
    static bool learning;

    /// <summary>
    /// Asks which of a repository's folders git ignores. Tests swap it.
    /// </summary>
    internal static Func<string, IReadOnlyCollection<string>, Dictionary<string, bool>> AskIgnored = LibGitState.Ignored;

    /// <summary>
    /// Whether git ignores <paramref name="folder"/>, so that changes in it are dropped. Null
    /// until it has been looked at.
    /// </summary>
    internal static bool? Ignored(string folder)
    {
        lock (gate)
        {
            if (folders.TryGetValue(folder, out var ignored))
            {
                return ignored;
            }

            return null;
        }
    }

    // What git ignores in the repository may be other than it was: a .gitignore changed, or its
    // exclude file or config, or its index, as a file added in an ignored folder is one git
    // tracks from then on. Or it is to be read whatever. Under the gate.
    static void Forget(Known entry)
    {
        foreach (var folder in entry.Learned)
        {
            folders.Remove(folder);
        }

        entry.Learned.Clear();
        entry.Forgotten++;
    }

    static bool ChangesWhatIsIgnored(CharSpan insideGit) =>
        insideGit.Equals("index", StringComparison.OrdinalIgnoreCase) ||
        insideGit.Equals("config", StringComparison.OrdinalIgnoreCase) ||
        insideGit.Equals(@"info\exclude", StringComparison.OrdinalIgnoreCase);

    // Under the gate. The folder is looked at once, however many changes come in it meanwhile.
    static void Learn(string repo, string folder)
    {
        if (!toLearn.TryAdd(folder, repo) ||
            learning)
        {
            return;
        }

        learning = true;
        Task.Run(Learn);
    }

    static void Learn()
    {
        while (true)
        {
            List<IGrouping<string, string>> asked;
            var forgotten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                if (toLearn.Count == 0)
                {
                    learning = false;
                    return;
                }

                asked = toLearn
                    .GroupBy(_ => _.Value, _ => _.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                toLearn.Clear();
                foreach (var group in asked)
                {
                    if (known.TryGetValue(group.Key, out var entry))
                    {
                        forgotten[group.Key] = entry.Forgotten;
                    }
                }
            }

            foreach (var group in asked)
            {
                if (!forgotten.TryGetValue(group.Key, out var before))
                {
                    continue;
                }

                var found = AskIgnored(group.Key, group.ToList());
                lock (gate)
                {
                    // Forgotten meanwhile, what was found is of how it was.
                    if (!known.TryGetValue(group.Key, out var entry) ||
                        entry.Forgotten != before)
                    {
                        continue;
                    }

                    foreach (var (folder, ignored) in found)
                    {
                        if (folders.TryAdd(folder, ignored))
                        {
                            entry.Learned.Add(folder);
                        }
                    }
                }
            }
        }
    }

    // The read of each repository under way, the latest begun where there are two.
    static Dictionary<string, Reading> reading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <paramref name="repo"/> with <paramref name="read"/>, or waits for a read of it
    /// already under way and gives what that finds: two views showing the one repository, the
    /// code folder in two windows say, are both told of a change in it, and read it once
    /// between them. Not one begun before the last change seen, which may have missed it, nor
    /// one whose view let go of it, which may have stopped short: then it is read again. With
    /// what was found, how many changes had been seen as the read began, for
    /// <see cref="Store"/>. Nothing found when <paramref name="cancel"/> is cancelled while
    /// waiting, as a read stopped finds nothing.
    /// </summary>
    public static (GitState? State, RemoteSite? Site, int Changes) Read(string repo, Cancel cancel, Func<(GitState? State, RemoteSite? Site)> read)
    {
        while (true)
        {
            var changes = Changes(repo);
            Reading? mine = null;
            Reading? theirs;
            lock (gate)
            {
                if (!reading.TryGetValue(repo, out theirs) ||
                    theirs.Changes != changes ||
                    theirs.Cancel.IsCancellationRequested)
                {
                    mine = new(changes, cancel);
                    reading[repo] = mine;
                }
            }

            if (mine != null)
            {
                return ReadAs(repo, mine, read);
            }

            try
            {
                theirs!.Done.Task.Wait(cancel);
            }
            catch (OperationCanceledException)
            {
                return (null, null, changes);
            }
            catch (AggregateException)
            {
                // It failed for whoever was reading. Read again, to fail, or not, for this one.
                continue;
            }

            if (theirs.Cancel.IsCancellationRequested)
            {
                continue;
            }

            var found = theirs.Done.Task.Result;
            return (found.State, found.Site, changes);
        }
    }

    static (GitState? State, RemoteSite? Site, int Changes) ReadAs(string repo, Reading mine, Func<(GitState? State, RemoteSite? Site)> read)
    {
        try
        {
            var found = read();
            mine.Done.SetResult(found);
            return (found.State, found.Site, mine.Changes);
        }
        catch (Exception exception)
        {
            mine.Done.SetException(exception);
            throw;
        }
        finally
        {
            lock (gate)
            {
                if (reading.TryGetValue(repo, out var latest) &&
                    latest == mine)
                {
                    reading.Remove(repo);
                }
            }
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

    // What Find in Files last listed of each repository git doesn't ignore, kept while nothing in
    // it has changed, as the watch hears, nor what it ignores: the entry and its counts as they
    // were when it was listed.
    static Dictionary<string, (GitScope Scope, Known Entry, int Changes, int Forgotten)> searchable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What git doesn't ignore in <paramref name="repo"/>, for Find in Files: listed by
    /// <paramref name="list"/>, and kept, in the code folder, until something changes in the
    /// repository, so searching it again, as what is looked for is typed say, doesn't start git
    /// every time. Anywhere else nothing hears its changes, so it is listed every time. Null when
    /// it couldn't be listed.
    /// </summary>
    public static GitScope? Searchable(string repo, Func<GitScope?> list)
    {
        lock (gate)
        {
            if (!Heard(repo))
            {
                searchable.Remove(repo);
                return list();
            }
        }

        // Counted from now on, if no view has read it yet.
        var changes = Changes(repo);
        Known? entry;
        int forgotten;
        lock (gate)
        {
            if (!known.TryGetValue(repo, out entry))
            {
                return list();
            }

            forgotten = entry.Forgotten;
            if (searchable.TryGetValue(repo, out var kept) &&
                kept.Entry == entry &&
                kept.Changes == changes &&
                kept.Forgotten == forgotten)
            {
                return kept.Scope;
            }
        }

        var scope = list();
        if (scope == null)
        {
            return null;
        }

        lock (gate)
        {
            // As of the changes heard by the time it was listed, not before: listing changes
            // .git\refs itself, which counts as a change, and kept as of before it, it would be
            // listed every time. One made to the files while it was listed may be missed, till
            // the next change, or Refresh.
            if (known.TryGetValue(repo, out var now) &&
                now == entry &&
                entry.Forgotten == forgotten)
            {
                searchable[repo] = (scope, entry, entry.Changes, forgotten);
            }
        }

        return scope;
    }

    /// <summary>
    /// Lets go of what Find in Files listed, for it to be listed again: Refresh on its results.
    /// </summary>
    public static void ForgetSearchable()
    {
        lock (gate)
        {
            searchable.Clear();
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
                    Forget(entry);
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

    // On a background thread, for everything changed in the code folder, so as little as it can
    // be. Stale at once, so a view going to it reads it; the views showing it are told once its
    // changes stop.
    internal static void ChangedAt(string path)
    {
        lock (gate)
        {
            // Nothing is made for a change, not a string for each folder above it either: there
            // is one for everything a build writes.
            string? codeFolder = null;
            foreach (var folder in watchers.Keys)
            {
                if (Locations.IsWithin(path, folder))
                {
                    codeFolder = folder;
                    break;
                }
            }

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

            // The innermost repository it is in: one nested in another is its own. In a .git, it
            // may be in the git folder of a worktree or a submodule, which is then the one
            // changed, or be a branch or the config that worktrees share with what they are of.
            var now = Environment.TickCount64;
            var repos = known.GetAlternateLookup<CharSpan>();
            var gits = gitFolders.GetAlternateLookup<CharSpan>();
            var shared = sharing.GetAlternateLookup<CharSpan>();
            var looked = folders.GetAlternateLookup<CharSpan>();

            // In a folder git ignores, or one itself, a build's output say, it is no change to
            // the repository: nothing is stale, and nothing read. The folder it is in looked
            // at and not ignored, none above that is either, so they aren't asked after.
            var above = !inGit &&
                        folders.Count > 0;
            var folderLooked = false;
            if (above)
            {
                if (looked.TryGetValue(path, out var itself) &&
                    itself)
                {
                    return;
                }

                if (looked.TryGetValue(Path.GetDirectoryName(path.AsSpan()), out var folderIgnored))
                {
                    if (folderIgnored)
                    {
                        return;
                    }

                    folderLooked = true;
                    above = false;
                }
            }

            for (var current = path.AsSpan(); !current.IsEmpty && Locations.IsWithin(current, codeFolder); current = Path.GetDirectoryName(current))
            {
                if (inGit &&
                    gits.TryGetValue(current, out var own))
                {
                    // Its own objects and logs are as much noise as any.
                    if (IsNoiseInGit(Beneath(path, current)))
                    {
                        return;
                    }

                    if (ChangesWhatIsIgnored(Beneath(path, current)) &&
                        known.TryGetValue(own.Repo, out var its))
                    {
                        Forget(its);
                    }

                    Mark(own.Repo, inGit, now);

                    // A worktree's index and HEAD are its own alone. A submodule's HEAD is also
                    // the commit the repository it is in has it at, so that is changed too.
                    if (own.Shares)
                    {
                        return;
                    }

                    continue;
                }

                if (inGit &&
                    shared.TryGetValue(current, out var worktrees) &&
                    IsShared(Beneath(path, current)))
                {
                    foreach (var worktree in worktrees)
                    {
                        Mark(worktree, inGit, now);
                    }
                }

                // Further down in a folder git ignores. A repository nested in such a folder is
                // found first, being further in.
                if (!repos.TryGetValue(current, out var repo, out var entry))
                {
                    if (above &&
                        looked.TryGetValue(current, out var ignored) &&
                        ignored)
                    {
                        return;
                    }

                    continue;
                }

                if (inGit)
                {
                    if (ChangesWhatIsIgnored(inside))
                    {
                        Forget(entry);
                    }
                }
                else
                {
                    LearnOf(path, repo, entry, folderLooked);
                }

                Mark(repo, entry, inGit, now);
                return;
            }
        }
    }

    // A change to the repository's files, counted as one. A .gitignore changes what is ignored.
    // The folder of anything else is looked at, unless it has been, so that if git ignores it
    // the next change in it isn't counted. Under the gate.
    static void LearnOf(string path, string repo, Known entry, bool folderLooked)
    {
        if (Path.GetFileName(path.AsSpan()).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
        {
            Forget(entry);
            return;
        }

        if (folderLooked)
        {
            return;
        }

        var folder = Path.GetDirectoryName(path.AsSpan());
        if (folder.Length <= repo.Length ||
            folders.GetAlternateLookup<CharSpan>().ContainsKey(folder))
        {
            return;
        }

        Learn(repo, folder.ToString());
    }

    // What of path is beneath folder, which it is in.
    static CharSpan Beneath(CharSpan path, CharSpan folder)
    {
        if (path.Length <= folder.Length)
        {
            return default;
        }

        return path[folder.Length..].TrimStart(Path.DirectorySeparatorChar);
    }

    // Whether what changed in a git folder is what its worktrees have of it too: the branches,
    // so what each is ahead of and behind, and the config. Not its index or HEAD, which each
    // has its own of.
    static bool IsShared(CharSpan inside) =>
        Locations.IsWithin(inside, "refs") ||
        inside.Equals("packed-refs", StringComparison.OrdinalIgnoreCase) ||
        inside.Equals("config", StringComparison.OrdinalIgnoreCase);

    // Under the gate.
    static void Mark(string repo, bool inGit, long now)
    {
        if (known.TryGetValue(repo, out var entry))
        {
            Mark(repo, entry, inGit, now);
        }
    }

    // Under the gate.
    static void Mark(string repo, Known entry, bool inGit, long now)
    {
        entry.Changed(now, inGit);
        changing[repo] = entry;

        // Left as it is set when that is soon enough, as it nearly always is: it then finds
        // this one not yet due, and waits on.
        if (entry.Due < settlingAt)
        {
            SettleAt(entry.Due, now);
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

    // Too many changes at once in a code folder to keep track of, or its watch failed or was
    // let go of and is back: anything in it may have changed. Every repository in it is stale
    // at once, and the views are told as they are of any change, once it has settled: too many
    // changes come again and again while whatever is making them goes on, and each telling is a
    // read of all that are shown.
    internal static void AllChanged(string folder)
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            var first = long.MaxValue;
            foreach (var (repo, entry) in known)
            {
                if (!Locations.IsWithin(repo, folder))
                {
                    continue;
                }

                entry.AnyChanged(now);
                Forget(entry);
                changing[repo] = entry;
                first = Math.Min(first, entry.Due);
            }

            if (first < settlingAt)
            {
                SettleAt(first, now);
            }
        }
    }

    // The repositories in a code folder are to be read whatever has been seen of them: its
    // watch is new, back, or gone. Under the gate.
    static void MarkStaleIn(string folder)
    {
        foreach (var (repo, entry) in known)
        {
            if (!Locations.IsWithin(repo, folder))
            {
                continue;
            }

            entry.Ask();
            Forget(entry);
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
            gitFolders.Clear();
            sharing.Clear();
            reading.Clear();
            folders.Clear();
            toLearn.Clear();
            changing.Clear();
            foreach (var watcher in watchers.Values)
            {
                watcher?.Dispose();
            }

            watchers.Clear();
        }
    }
}
