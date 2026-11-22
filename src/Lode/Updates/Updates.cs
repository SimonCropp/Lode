/// <summary>
/// Whether a newer Lode is on NuGet, asked once a day, and the update to it. The update is
/// <see cref="ToolUpdate"/>, as DiffEngineTray's and BuildMonitor's are: a hidden
/// PowerShell waits for Lode to exit, runs <c>dotnet tool update</c> by the tool path Lode is in,
/// and starts Lode again, which reopens the tabs it had. How that went is written to
/// <see cref="OutcomePath"/>, for the Lode it starts to say (<see cref="Report"/>).
/// <para>
/// Only a copy installed as a dotnet tool asks or updates. A build's output has nothing to update,
/// so it would only ever be told of a version it cannot get.
/// </para>
/// </summary>
sealed class Updates
{
    static TimeSpan interval = TimeSpan.FromDays(1);

    // The first check waits until Lode has started and settled; after that, whether a day has
    // passed is looked at each hour, since Lode runs in the tray for days.
    static TimeSpan firstDelay = TimeSpan.FromSeconds(15);
    static TimeSpan pollInterval = TimeSpan.FromHours(1);

    Settings settings;
    Func<Cancel, Task<NuGetVersion?>> fetchLatest;
    TimeProvider time;
    Timer? timer;

    /// <summary>
    /// The one every window and Options shows, put in by <see cref="Program"/> before the first
    /// window opens. Until then, and in tests, one that finds nothing and never asks.
    /// </summary>
    public static Updates Instance { get; internal set; } = new(new(), null, RunningVersion(), _ => Task.FromResult<NuGetVersion?>(null), TimeProvider.System);

    /// <param name="tool">The update for this copy, or null when it is not an installed tool.</param>
    /// <param name="current">The version running.</param>
    /// <param name="fetchLatest">The newest version on NuGet, prereleases included, since the
    /// update installs those too. Replaced in tests.</param>
    public Updates(Settings settings, ToolUpdate? tool, NuGetVersion current, Func<Cancel, Task<NuGetVersion?>> fetchLatest, TimeProvider time)
    {
        this.settings = settings;
        this.fetchLatest = fetchLatest;
        this.time = time;
        Tool = tool;
        Current = current;

        // What the last check found, so a pending update shows from the start rather than a day
        // later. Not once it is the version running, which it is after updating.
        if (NuGetVersion.TryParse(settings.AvailableUpdate, out var remembered))
        {
            Available = Newer(remembered);
        }
    }

    public static Updates ForThisCopy(Settings settings) =>
        new(settings, Find(AppContext.BaseDirectory), RunningVersion(), Latest, TimeProvider.System);

    /// <summary>
    /// The update for the tool whose store <paramref name="directory"/> is in, or null for a
    /// build's output, which is in none.
    /// </summary>
    internal static ToolUpdate? Find(string directory)
    {
        var shim = ToolUpdate.ShimFor(directory);
        if (shim == null)
        {
            return null;
        }

        return new(shim, OutcomePath);
    }

    /// <summary>
    /// Beside the settings, outside the store the update replaces.
    /// </summary>
    public static string OutcomePath => Path.Combine(Settings.Folder, "update-outcome.log");

    /// <summary>
    /// The version running, from the informational version, which a prerelease keeps and the
    /// assembly version drops.
    /// </summary>
    static NuGetVersion RunningVersion()
    {
        var assembly = typeof(Updates).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (NuGetVersion.TryParse(informational, out var version))
        {
            return version;
        }

        return new(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    public ToolUpdate? Tool { get; }

    public NuGetVersion Current { get; }

    /// <summary>
    /// A version newer than <see cref="Current"/>, or null when there is none, or none known.
    /// </summary>
    public NuGetVersion? Available { get; private set; }

    /// <summary>
    /// Why the last check failed, or null when it did not.
    /// </summary>
    public string? Error { get; private set; }

    public bool Checking { get; private set; }

    public DateTimeOffset? LastChecked => settings.LastUpdateCheck;

    /// <summary>
    /// For showing <see cref="LastChecked"/> in the local time zone, which tests pin.
    /// </summary>
    public TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

    /// <summary>
    /// Raised on the UI thread when a check starts or ends.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Asks NuGet now if a day has passed since it was last asked, and from then on each hour
    /// looks again. Nothing for a copy that is not an installed tool.
    /// </summary>
    public void Start()
    {
        if (Tool == null)
        {
            return;
        }

        timer = new()
        {
            Interval = (int) firstDelay.TotalMilliseconds
        };
        timer.Tick += (_, _) =>
        {
            timer.Interval = (int) pollInterval.TotalMilliseconds;
            _ = CheckIfDue();
        };
        timer.Start();
    }

    /// <summary>
    /// Whether a day has passed since <paramref name="lastChecked"/>. A check that seems to be in
    /// the future, after the clock was put back, is due too, or it would not be due for as long
    /// as the clock went back.
    /// </summary>
    internal static bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset now) =>
        lastChecked == null ||
        lastChecked > now ||
        now - lastChecked >= interval;

    internal Task CheckIfDue()
    {
        if (!IsDue(settings.LastUpdateCheck, time.GetUtcNow()))
        {
            return Task.CompletedTask;
        }

        return Check();
    }

    /// <summary>
    /// Asks NuGet for the newest version, and remembers when and what it said. A failure, such
    /// as being offline, keeps what was known, and leaves the check due, so the next hour tries
    /// again.
    /// </summary>
    public async Task Check()
    {
        if (Checking)
        {
            return;
        }

        Checking = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            using var cancel = new CancelSource(TimeSpan.FromMinutes(1));
            var latest = await Task.Run(() => fetchLatest(cancel.Token), cancel.Token);
            Available = Newer(latest);
            settings.LastUpdateCheck = time.GetUtcNow();
            settings.AvailableUpdate = Available?.ToNormalizedString();
            settings.Save();
        }
        catch (Exception exception)
        {
            // A check in the background must never take Lode down.
            Error = exception.Message;
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    NuGetVersion? Newer(NuGetVersion? version)
    {
        if (Tool == null ||
            version == null ||
            version <= Current)
        {
            return null;
        }

        return version;
    }

    /// <summary>
    /// Starts the shell that updates, which waits for Lode to exit: exiting is the caller's.
    /// Throws what starting PowerShell throws.
    /// </summary>
    public void Apply()
    {
        if (Tool == null)
        {
            throw new InvalidOperationException("This copy of Lode is not an installed dotnet tool, so there is nothing to update.");
        }

        Tool.Start();
    }

    /// <summary>
    /// What to say about the update that started this Lode, or null when none did, which is every
    /// start but the one after an update, or when how it went could not be read or removed, which
    /// is no reason to stop Lode starting. Said once, since reading it removes it, so only the
    /// first instance asks: a second instance handing over and leaving at once would take it from
    /// the one that stays.
    /// </summary>
    public UpdateNotice? Report()
    {
        if (Tool == null)
        {
            return null;
        }

        ToolUpdateOutcome? outcome;
        try
        {
            outcome = ToolUpdateOutcome.Take(Tool.Outcome);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return outcome?.Notice(Current.ToNormalizedString());
    }

    /// <summary>
    /// The newest listed version on nuget.org, which is where Lode is published, prereleases
    /// included, as <c>dotnet tool update --prerelease</c> takes them.
    /// </summary>
    static Task<NuGetVersion?> Latest(Cancel cancel) =>
        NuGetFeed.LatestVersion(ToolUpdate.PackageId, cancel);
}
