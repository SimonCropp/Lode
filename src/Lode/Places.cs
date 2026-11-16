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
    /// again: after a folder was pinned or unpinned from one of Lode's menus, and at the start
    /// if they are not as they were remembered.
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

    public static Places ForThisMachine(Settings settings)
    {
        var places = Remembering(settings, QuickAccessFolders);
        ShellContextMenu.QuickAccessChanged += places.Reread;
        return places;
    }

    /// <summary>
    /// Starts with what Quick access had when it was last read, so the first window has its
    /// tree at once. Reading it takes a moment, and is done behind the window, off the UI
    /// thread; the tree follows if it turns out to have changed. Made on the UI thread.
    /// </summary>
    /// <param name="read">Reads Quick access, on a thread of its own. Given in tests.</param>
    internal static Places Remembering(Settings settings, Func<(List<string> Pinned, List<string> Recent)> read)
    {
        var places = new Places(settings.PinnedFolders.ToList(), settings.RecentFolders.ToList(), ShowComputer: true, DescribeFreeSpace)
        {
            settings = settings,
            read = read,
            context = new WindowsFormsSynchronizationContext()
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

    // On a thread of its own, set up for the shell's COM objects as the UI thread is. In the
    // background, so a slow shell does not keep Lode from exiting.
    void Read(bool evenIfUnchanged)
    {
        var version = ++reading;
        var thread = new Thread(
            () =>
            {
                var (pinned, recent) = read!();
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
                // Quick access also lists recent files, and can hold virtual folders such as
                // the Recycle Bin, which have no path to browse. A machine on the network is not
                // part of the file system either, but Lode lists its shares.
                if (Get(item, "IsFolder") is not true ||
                    Get(item, "Path") is not string path ||
                    (Get(item, "IsFileSystem") is not true && !Locations.IsServer(path)))
                {
                    continue;
                }

                // An offline share takes a long time to say it isn't there, so network
                // folders are taken on trust.
                if (!Locations.IsNetwork(path) && !Directory.Exists(path))
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

    static string DescribeFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is { Length: > 0 } && !Locations.IsNetwork(root))
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
