/// <summary>
/// The parts of the window that describe the machine rather than the folder being browsed:
/// the pinned and recent folders at the top of the tree, whether drives are listed, and how
/// free space is reported. Passed in so tests can show a fixed set.
/// </summary>
sealed record Places(
    IReadOnlyList<string> Pinned,
    IReadOnlyList<string> Recent,
    bool ShowComputer,
    Func<string, string> FreeSpace,
    IReadOnlyList<string>? Drives = null)
{
    /// <summary>
    /// The roots listed under This PC: the machine's drives, unless a test gave its own.
    /// </summary>
    public IReadOnlyList<string> DrivePaths() =>
        Drives ?? DriveInfo.GetDrives().Select(_ => _.Name).ToList();

    // The shell folder behind Explorer's Quick access list.
    const string quickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public IReadOnlyList<string> Pinned { get; private set; } = Pinned;
    public IReadOnlyList<string> Recent { get; private set; } = Recent;

    /// <summary>
    /// Raised on the UI thread once <see cref="Pinned"/> and <see cref="Recent"/> have been read
    /// again: after a folder was pinned or unpinned from one of Lode's menus, and otherwise
    /// only when they are not as they were: at the start, on F5, when a window comes to the
    /// front, and after Lode moved, renamed or deleted one of them.
    /// </summary>
    public event Action? Changed;

    // Where what Quick access has is kept for the next start, how it is read, and how to get
    // back to the UI thread once it has been. None of them for places a test made up, which
    // are never read again.
    Settings? settings;
    Func<(List<string> Pinned, List<string> Recent)>? read;
    SynchronizationContext? context;

    // Which reading of Quick access is the latest asked for. One overtaken, by a folder pinned
    // straight after another say, is dropped.
    int reading;

    // When Quick access was last asked for, by the clock given in tests.
    TimeProvider time = TimeProvider.System;
    DateTimeOffset lastRead;

    /// <summary>
    /// How long Quick access is taken to be as it was read, as far as a window coming to the
    /// front goes.
    /// </summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long after Lode moved, renamed or deleted a pinned or recent folder Quick access is
    /// read again. Tests have it at once.
    /// </summary>
    internal TimeSpan FollowedAfter { get; set; } = TimeSpan.FromSeconds(2);

    public static Places ForThisMachine(Settings settings)
    {
        var places = Remembering(settings, QuickAccessFolders);
        ShellContextMenu.QuickAccessChanged += places.Reread;
        ShellFileOperation.Gone += places.ReadIfGone;
        return places;
    }

    /// <summary>
    /// Starts with what Quick access had when it was last read, so the first window has its
    /// tree at once. Reading it takes a moment, and is done behind the window, off the UI
    /// thread; the tree follows if it turns out to have changed. Made on the UI thread.
    /// </summary>
    /// <param name="read">Reads Quick access, on a thread of its own. Given in tests.</param>
    /// <param name="time">The clock, given in tests.</param>
    /// <param name="drives">The drives under This PC, given in tests.</param>
    internal static Places Remembering(
        Settings settings,
        Func<(List<string> Pinned, List<string> Recent)> read,
        TimeProvider? time = null,
        IReadOnlyList<string>? drives = null)
    {
        var places = new Places(settings.PinnedFolders.ToList(), settings.RecentFolders.ToList(), ShowComputer: true, DescribeFreeSpace, drives)
        {
            settings = settings,
            read = read,
            context = new WindowsFormsSynchronizationContext(),
            time = time ?? TimeProvider.System
        };
        places.Read(evenIfUnchanged: false);
        return places;
    }

    /// <summary>
    /// Reads Quick access again, after a folder was pinned or unpinned. Always says so once it
    /// has been read, changed or not: the tree is waiting to hear.
    /// </summary>
    internal void Reread() =>
        Read(evenIfUnchanged: true);

    /// <summary>
    /// Reads Quick access again for F5, as anything else is read again then: a folder pinned
    /// since in Explorer or a file dialog, unpinned there, or used lately. Says so only when it
    /// has changed.
    /// </summary>
    internal void ReadAgain() =>
        Read(evenIfUnchanged: false);

    /// <summary>
    /// Reads Quick access again as a window comes to the front, back from the tray or another
    /// app, where folders may have been pinned or used meanwhile, unless it was read in the
    /// last few minutes: going from one window to another and back reads it once.
    /// </summary>
    internal void ReadAgainIfStale()
    {
        if (time.GetUtcNow() - lastRead < FreshFor)
        {
            return;
        }

        Read(evenIfUnchanged: false);
    }

    /// <summary>
    /// Reads Quick access again after Lode moved, renamed or deleted what is given, where a
    /// pinned or recent folder was one of those or in one: Quick access follows a folder moved,
    /// and a folder deleted is no longer listed. Heard on the operation's thread, and looked
    /// into back on the UI thread. Read a moment after, as the shell itself follows the folder
    /// in Quick access only once it has heard of the move from the operation.
    /// </summary>
    internal void ReadIfGone(IReadOnlyList<string> gone)
    {
        try
        {
            context?.Post(_ => ReadIfAmong(gone), null);
        }
        catch (InvalidOperationException)
        {
            // Lode is exiting, and there is no longer a UI thread to tell.
        }
    }

    void ReadIfAmong(IReadOnlyList<string> gone)
    {
        if (Pinned.Concat(Recent).Any(_ => gone.Any(place => Locations.IsWithin(_, place))))
        {
            Read(evenIfUnchanged: false, after: FollowedAfter);
        }
    }

    // On a thread of its own, set up for the shell's COM objects as the UI thread is. In the
    // background, so a slow shell does not keep Lode from exiting. Not for places a test made
    // up, which have nothing to read.
    void Read(bool evenIfUnchanged, TimeSpan after = default)
    {
        if (read is not { } readQuickAccess)
        {
            return;
        }

        var version = ++reading;
        lastRead = time.GetUtcNow();
        var thread = new Thread(
            () =>
            {
                if (after > TimeSpan.Zero)
                {
                    Thread.Sleep(after);
                }

                var (pinned, recent) = readQuickAccess();
                try
                {
                    context!.Post(_ => Take(version, pinned, recent, evenIfUnchanged), null);
                }
                catch (InvalidOperationException)
                {
                    // Lode is exiting, and there is no longer a UI thread to tell.
                }
            })
        {
            IsBackground = true,
            Name = "Lode Quick access"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    void Take(int version, List<string> pinned, List<string> recent, bool evenIfUnchanged)
    {
        if (version != reading)
        {
            return;
        }

        var unchanged = pinned.SequenceEqual(Pinned, StringComparer.OrdinalIgnoreCase) &&
                        recent.SequenceEqual(Recent, StringComparer.OrdinalIgnoreCase);
        Pinned = pinned;
        Recent = recent;
        settings!.PinnedFolders = pinned;
        settings.RecentFolders = recent;
        if (evenIfUnchanged ||
            !unchanged)
        {
            Changed?.Invoke();
        }
    }

    // Calls on a shell automation object by name, as a script makes them. Not through dynamic,
    // which would load the C# runtime binder, megabytes of it, on every start.
    static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);

    /// <summary>
    /// The folders in Explorer's Quick access list, in Explorer's order, split into the ones the
    /// user pinned and the frequent ones Explorer adds itself. Pinning or unpinning a folder in
    /// Explorer is how the pinned list is edited.
    /// </summary>
    internal static (List<string> Pinned, List<string> Recent) QuickAccessFolders()
    {
        var pinned = new List<string>();
        var recent = new List<string>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
            {
                return (pinned, recent);
            }

            var shell = Activator.CreateInstance(type)!;
            var folder = Call(shell, "Namespace", quickAccess)!;
            foreach (var item in (IEnumerable)Call(folder, "Items")!)
            {
                if (FolderToList(item) is not { } path)
                {
                    continue;
                }

                // Windows 11 says which folders are pinned. Older versions don't, so there every
                // folder counts as pinned, as Explorer showed them all together.
                var isPinned = Call(item, "ExtendedProperty", "System.Home.IsPinned");
                (isPinned is false ? recent : pinned).Add(path);
            }
        }
        catch (Exception)
        {
            // Without Quick access the tree still has This PC.
        }

        return (pinned, recent);
    }

    /// <summary>
    /// Whether a folder is somewhere on the network, a share or a drive mapped to one. Tests
    /// swap it, having no mapped drive.
    /// </summary>
    internal static Func<string, bool> IsRemote = Locations.IsRemote;

    /// <summary>
    /// Whether a folder is there. Tests swap it, to see what is looked for.
    /// </summary>
    internal static Func<string, bool> FolderExists = Directory.Exists;

    /// <summary>
    /// The path of a shell item to list as a folder, or null. Quick access also lists recent
    /// files, and can hold virtual folders such as the Recycle Bin, which have no path to browse.
    /// A machine on the network is not part of the file system either, but Lode lists its
    /// shares. A .zip is a folder in the file system to the shell, one it browses into, and is
    /// left out by that, as a folder on the network is not looked for: asking a share, or a drive
    /// mapped to one, whether a folder is there waits, when it can't be reached, off the VPN say,
    /// for as long as the network takes to give up, and pinning reads Quick access too.
    /// </summary>
    internal static string? FolderToList(object item)
    {
        if (Get(item, "IsFolder") is not true ||
            Get(item, "Path") is not string path)
        {
            return null;
        }

        if (Get(item, "IsFileSystem") is true)
        {
            // A real folder is not "browsable" to the shell; a .zip, browsed into, is.
            if (Get(item, "IsBrowsable") is true)
            {
                return null;
            }
        }
        else if (!Locations.IsServer(path))
        {
            return null;
        }

        if (!IsRemote(path) &&
            !FolderExists(path))
        {
            return null;
        }

        return path;
    }

    static string DescribeFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is { Length: > 0 } &&
                !Locations.IsNetwork(root))
            {
                var drive = new DriveInfo(root);
                return $"{Formatting.Size(drive.AvailableFreeSpace)} free of {Formatting.Size(drive.TotalSize)}";
            }
        }
        catch (Exception)
        {
            // Free space is informational only.
        }

        return "";
    }
}
