/// <summary>
/// Fetches the repositories open in tabs every so often, in the background, as GitHub Desktop
/// does about hourly, so how far ahead and behind origin each branch is, which the status bar
/// and the Git column count as of the last fetch, stays close to what origin has. Quietly:
/// nothing in the Actions panel, no dialog when it fails, and never a prompt, git's own or a
/// credential helper's, for nobody asked for it, and nobody may be there to answer. A
/// repository is fetched once <see cref="Settings.FetchMinutes"/> have passed since it was last
/// fetched, by anything, as git's FETCH_HEAD says, or last tried here; never two at once, a
/// repository and its worktree being one, nor while the Actions panel runs anything on it. Only
/// in the code folder, as all of Lode's git is.
/// </summary>
sealed class BackgroundFetch : IDisposable
{
    Settings settings;
    Func<IReadOnlyList<string>> openFolders;
    TimeProvider time;
    Func<IReadOnlyList<string>> busy;
    Action<IReadOnlyList<string>> fetched;
    Func<string, Cancel, CodeCommands.RepoResult> fetch;

    // Whether it is time is looked at every minute: the interval is counted from each
    // repository's own last fetch, not from Lode starting.
    Timer timer = new()
    {
        Interval = 60_000
    };

    Lock gate = new();

    // Those being fetched, by the git directory they share with their worktrees, and when each
    // was last tried, so one that fails, offline say, waits the interval before it is tried again.
    HashSet<string> fetching = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, DateTimeOffset> tried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For the running app: the repositories the tabs of every window showing are in, fetched
    /// as <see cref="CodeCommands.FetchQuietly"/> does, skipping those an action in the Actions
    /// panel is running on, and read again once fetched.
    /// </summary>
    public BackgroundFetch(Settings settings) :
        this(settings, MainForm.OpenFolders, TimeProvider.System, RunningFolders, Fetched, CodeCommands.FetchQuietly)
    {
    }

    /// <param name="openFolders">The folders open, each standing for the repository it is in,
    /// asked on the UI thread.</param>
    /// <param name="busy">The folders Lode's own actions are running on, asked on the UI thread.</param>
    /// <param name="fetched">The repositories fetched, given on the thread that ticked, once
    /// their fetches have ended, whether or not each went well.</param>
    /// <param name="fetch">Fetches a repository, on a thread of its own.</param>
    internal BackgroundFetch(
        Settings settings,
        Func<IReadOnlyList<string>> openFolders,
        TimeProvider time,
        Func<IReadOnlyList<string>> busy,
        Action<IReadOnlyList<string>> fetched,
        Func<string, Cancel, CodeCommands.RepoResult> fetch)
    {
        this.settings = settings;
        this.openFolders = openFolders;
        this.time = time;
        this.busy = busy;
        this.fetched = fetched;
        this.fetch = fetch;
        timer.Tick += (_, _) => _ = Tick();
    }

    /// <summary>
    /// Starts looking every minute for repositories due to be fetched. On the UI thread.
    /// </summary>
    public void Start() =>
        timer.Start();

    /// <summary>
    /// How long a repository goes between fetches: <see cref="Settings.FetchMinutes"/>, no less
    /// than five minutes, nor more than a day, as Options has it.
    /// </summary>
    internal static TimeSpan Interval(Settings settings) =>
        TimeSpan.FromMinutes(Math.Clamp(settings.FetchMinutes, MinimumMinutes, MaximumMinutes));

    internal const int MinimumMinutes = 5;
    internal const int MaximumMinutes = 1440;

    /// <summary>
    /// Fetches each repository open that is due, unless it is off, or there is no code folder:
    /// what is open, and what Lode is running on, are asked here, on the UI thread, and the rest,
    /// which goes to the disk, is done on others. Completes once those it started have ended.
    /// </summary>
    internal Task Tick()
    {
        if (!settings.FetchInBackground ||
            settings.CodeDirectory is not { } codeFolder)
        {
            return Task.CompletedTask;
        }

        var folders = openFolders();
        if (folders.Count == 0)
        {
            return Task.CompletedTask;
        }

        var running = busy();
        var now = time.GetUtcNow();
        var interval = Interval(settings);
        var context = SynchronizationContext.Current;
        return Task.Run(() => FetchDue(folders, codeFolder, running, now, interval, context));
    }

    void FetchDue(IReadOnlyList<string> folders, string codeFolder, IReadOnlyList<string> running, DateTimeOffset now, TimeSpan interval, SynchronizationContext? context)
    {
        // Read before the turn is taken, as reading them goes to the disk.
        var runningShared = running
            .Select(CodeCommands.SharedDirectory)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = folders
            .Where(_ => !Locations.IsThisPC(_) &&
                        Locations.IsWithin(_, codeFolder))
            .Select(_ => CodeCommands.RepoContaining(_, codeFolder))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(_ => !running.Any(folder => Overlap(_, folder)))
            .Select(_ => (Repo: _, Shared: CodeCommands.SharedDirectory(_) ?? _, Fetched: LastFetched(_)))
            .Where(_ => !runningShared.Contains(_.Shared))
            .ToList();

        var due = new List<(string Repo, string Shared)>();
        lock (gate)
        {
            foreach (var (repo, shared, lastFetched) in candidates)
            {
                if (fetching.Contains(shared))
                {
                    continue;
                }

                var last = lastFetched;
                if (tried.TryGetValue(shared, out var lastTried) &&
                    (last == null || lastTried > last))
                {
                    last = lastTried;
                }

                if (last != null &&
                    now - last < interval)
                {
                    continue;
                }

                fetching.Add(shared);
                tried[shared] = now;
                due.Add((repo, shared));
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        try
        {
            CodeCommands.InParallel(due, _ => fetch(_.Repo, Cancel.None));
        }
        finally
        {
            lock (gate)
            {
                foreach (var (_, shared) in due)
                {
                    fetching.Remove(shared);
                }
            }
        }

        List<string> done = [.. due.Select(_ => _.Repo)];
        if (context == null)
        {
            fetched(done);
            return;
        }

        context.Post(_ => fetched(done), null);
    }

    // A folder an action runs on that is the repository, in it, or holds it.
    static bool Overlap(string repo, string folder) =>
        Locations.IsWithin(repo, folder) ||
        Locations.IsWithin(folder, repo);

    /// <summary>
    /// When the repository was last fetched, by Lode or git anywhere else, a terminal's fetch or
    /// pull say, as GitHub Desktop tells it: when git last wrote FETCH_HEAD, which every fetch
    /// does, in the worktree's own git directory or the one it shares. Null where it never has
    /// been, or that can't be read.
    /// </summary>
    internal static DateTimeOffset? LastFetched(string repo)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is not { } git)
            {
                return null;
            }

            DateTimeOffset? last = null;
            foreach (var directory in new[] {git, CodeCommands.CommonDirectory(git)})
            {
                var head = new FileInfo(Path.Combine(directory, "FETCH_HEAD"));
                if (!head.Exists)
                {
                    continue;
                }

                DateTimeOffset written = head.LastWriteTimeUtc;
                if (last == null ||
                    written > last)
                {
                    last = written;
                }
            }

            return last;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the Actions panel has running, as each folder it acts on.
    /// </summary>
    internal static List<string> RunningFolders() =>
        ActionLog.Entries
            .Where(_ => _.State == ActionLog.State.Running)
            .SelectMany(_ => _.Folders)
            .ToList();

    // As Fetch's are once it ends: read again, rather than once the watch has heard of each.
    static void Fetched(IReadOnlyList<string> repos)
    {
        GitCache.MarkStale(repos);
        MainForm.ReadGitStateInAllWindows();
    }

    public void Dispose() =>
        timer.Dispose();
}
