/// <summary>
/// Watches a folder for changes, and lets go of it while its drive is being removed, locked or
/// dismounted, so Lode never stops "Safely remove" with "this device is currently in use". When
/// that is called off it watches again and reports a change, since some may have been missed.
/// Throws, as FileSystemWatcher does, where the folder can't be watched.
/// </summary>
sealed class FolderWatcher : IDisposable
{
    string path;
    NotifyFilters filter;
    Action changed;
    Lock gate = new();
    IDisposable removal;
    FileSystemWatcher? watcher;
    bool disposed;

    /// <param name="changed">Called on a background thread for each change.</param>
    public FolderWatcher(string path, NotifyFilters filter, Action changed)
    {
        this.path = path;
        this.filter = filter;
        this.changed = changed;
        watcher = Start();
        var root = Path.GetPathRoot(path);
        removal = root is { Length: > 0 } && !Locations.IsNetwork(root)
            ? DriveRemoval.Subscribe(root, DriveChanged)
            : DriveRemoval.None;
    }

    FileSystemWatcher Start()
    {
        var started = new FileSystemWatcher(path)
        {
            NotifyFilter = filter
        };
        started.Created += (_, _) => changed();
        started.Deleted += (_, _) => changed();
        started.Changed += (_, _) => changed();
        started.Renamed += (_, _) => changed();

        // Too many changes at once to keep track of, or the folder itself has gone. Either way,
        // read it again.
        started.Error += (_, _) => changed();
        started.EnableRaisingEvents = true;
        return started;
    }

    /// <summary>
    /// Whether the folder is being watched now, rather than let go of for its drive.
    /// </summary>
    internal bool IsWatching
    {
        get
        {
            lock (gate)
            {
                return watcher != null;
            }
        }
    }

    // On the notification's thread, which Windows waits on before going ahead with the removal.
    internal void DriveChanged(bool going)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (going)
            {
                watcher?.Dispose();
                watcher = null;
                return;
            }

            if (watcher != null)
            {
                return;
            }

            try
            {
                watcher = Start();
            }
            catch (Exception)
            {
                // The drive went after all, or the folder with it.
            }
        }

        changed();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            watcher?.Dispose();
            watcher = null;
        }

        removal.Dispose();
    }
}
