/// <summary>
/// A watch on a folder: a <see cref="FolderWatcher"/>, or what stands in for one in tests.
/// </summary>
interface IFolderWatch : IDisposable
{
    /// <inheritdoc cref="FolderWatcher.Revive"/>
    void Revive();
}

/// <summary>
/// Watches a folder for changes, and lets go of it while its drive is being removed, locked or
/// dismounted, so Lode never stops "Safely remove" with "this device is currently in use". When
/// that is called off it watches again and reports a change, since some may have been missed.
/// It lets go the same way while Lode moves, renames or deletes the folder, or one above it:
/// see <see cref="LetGo"/>. Throws, as FileSystemWatcher does, where the folder can't be watched.
/// </summary>
sealed class FolderWatcher : IFolderWatch
{
    // Every folder watcher there is, for LetGo to find those beneath what is being moved.
    static Lock everyGate = new();
    static List<FolderWatcher> every = [];

    string path;
    NotifyFilters filter;
    string? name;
    Action changed;
    Action<string, string>? renamed;
    bool subfolders;
    Action<string, WatcherChangeTypes>? changedAt;
    Lock gate = new();
    IDisposable removal;
    FileSystemWatcher? watcher;
    bool disposed;
    bool driveGoing;

    // How many operations under way have it let go of the folder.
    int lettingGo;

    /// <param name="changed">Called on a background thread for each change.</param>
    /// <param name="name">Only changes to what has this name in the folder, rather than to
    /// anything in it.</param>
    /// <param name="renamed">Called on a background thread, from where and to where, for
    /// something renamed in the folder, before <paramref name="changed"/> is.</param>
    /// <param name="subfolders">Everything beneath the folder, not just what is in it.</param>
    /// <param name="changedAt">Called on a background thread with the path of each change, and
    /// what the change was, in place of <paramref name="changed"/>, which is then only for when
    /// anything may have changed: too many changes to keep track of, or watching again after a
    /// time without.</param>
    public FolderWatcher(string path, NotifyFilters filter, Action changed, string? name = null, Action<string, string>? renamed = null, bool subfolders = false, Action<string, WatcherChangeTypes>? changedAt = null)
    {
        this.path = path;
        this.filter = filter;
        this.name = name;
        this.changed = changed;
        this.renamed = renamed;
        this.subfolders = subfolders;
        this.changedAt = changedAt;
        watcher = Start();
        removal = SubscribeToDrive();
        lock (everyGate)
        {
            every.Add(this);
        }
    }

    /// <summary>
    /// What tells it the drive is going, for tests.
    /// </summary>
    internal IDisposable Removal
    {
        get
        {
            lock (gate)
            {
                return removal;
            }
        }
    }

    // Not for somewhere on the network, a share or a drive mapped to one: there is no device
    // to remove, and opening the drive's root to hear of it waits on the network.
    IDisposable SubscribeToDrive()
    {
        var root = Path.GetPathRoot(path);
        if (root is { Length: > 0 } &&
            !Locations.IsRemote(root))
        {
            return DriveRemoval.Subscribe(root, DriveChanged);
        }

        return DriveRemoval.None;
    }

    FileSystemWatcher Start()
    {
        var started = new FileSystemWatcher(path)
        {
            NotifyFilter = filter,
            IncludeSubdirectories = subfolders
        };
        if (name != null)
        {
            started.Filter = name;
        }

        if (subfolders)
        {
            // Everything beneath a folder changes far more than what is in it: the most Windows
            // allows, so fewer are lost.
            started.InternalBufferSize = 64 * 1024;
        }

        if (changedAt is { } at)
        {
            started.Created += (_, args) => at(args.FullPath, args.ChangeType);
            started.Deleted += (_, args) => at(args.FullPath, args.ChangeType);
            started.Changed += (_, args) => at(args.FullPath, args.ChangeType);
            started.Renamed += (_, args) =>
            {
                renamed?.Invoke(args.OldFullPath, args.FullPath);
                at(args.OldFullPath, args.ChangeType);
                at(args.FullPath, args.ChangeType);
            };
        }
        else
        {
            started.Created += (_, _) => changed();
            started.Deleted += (_, _) => changed();
            started.Changed += (_, _) => changed();
            started.Renamed += (_, args) =>
            {
                renamed?.Invoke(args.OldFullPath, args.FullPath);
                changed();
            };
        }

        started.Error += (_, args) => Failed(started, args.GetException());
        started.EnableRaisingEvents = true;
        return started;
    }

    // Too many changes at once to keep track of, or the watch itself has failed: the folder
    // deleted, say, or the connection to its share dropped. Either way it is read again. A
    // watch that failed reports nothing more, even once the folder is back, so it is let go of
    // and started again. Where the folder is not back yet, Revive starts it on the next read.
    void Failed(FileSystemWatcher failed, Exception exception)
    {
        if (exception is not InternalBufferOverflowException)
        {
            lock (gate)
            {
                if (watcher == failed)
                {
                    watcher = null;
                    failed.Dispose();
                    if (!disposed)
                    {
                        Refresh();
                    }
                }
            }
        }

        changed();
    }

    /// <summary>
    /// Watches again where the watch failed and could not be started again then, the folder not
    /// being back yet, say. For a read of the folder that is about to be done, so reports no
    /// change. Does nothing while it is watching, or has let go on purpose. Starting a watch
    /// can wait on the drive, so not for the UI thread.
    /// </summary>
    public void Revive()
    {
        IDisposable? removed = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            // The drive was removed, and is back, plugged in again: another device, which the
            // old subscription hears nothing of, so the watch would hold it when it next goes.
            if (driveGoing &&
                DriveRemoval.DeviceRemoved(removal) &&
                Directory.Exists(Path.GetPathRoot(path)))
            {
                removed = removal;
                removal = SubscribeToDrive();
                driveGoing = false;
            }

            Refresh();
        }

        // Outside the gate: letting go of a registration waits for its callbacks, which take it.
        removed?.Dispose();
    }

    /// <summary>
    /// Whether the folder is being watched now, rather than let go of.
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
        bool report;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            driveGoing = going;
            report = Refresh();
        }

        if (report)
        {
            changed();
        }
    }

    // Watches, or lets go, as the drive and the operations under way allow. Under the gate. True
    // when it has just tried to watch again, which is reported as a change: some may have been
    // missed while it was not watching, or the folder may have gone.
    bool Refresh()
    {
        if (driveGoing ||
            lettingGo > 0)
        {
            watcher?.Dispose();
            watcher = null;
            return false;
        }

        if (watcher != null)
        {
            return false;
        }

        try
        {
            watcher = Start();
        }
        catch (Exception)
        {
            // The drive went after all, or the folder with it.
        }

        return true;
    }

    // What is watched: the folder, or what has the name in it.
    string Watched
    {
        get
        {
            if (name == null)
            {
                return path;
            }

            return Path.Join(path, name);
        }
    }

    /// <summary>
    /// Lets go of every folder watched at or beneath <paramref name="folders"/> until the result
    /// is disposed, and of every watch for one of them by name in the folder it is in. Each is
    /// then watched again, if it is still there, and reports a change. Windows won't move a
    /// folder, rename it or send it to the Recycle Bin while anything beneath it is watched, so
    /// Lode lets go of its own watches for its own operations: a tab showing a folder inside the
    /// one being deleted, or a folder expanded beneath it in the tree, would otherwise stop it.
    /// A watch for it by name doesn't stop it, but is quiet until the operation is done, which
    /// tells what went where.
    /// </summary>
    public static IDisposable LetGo(IReadOnlyCollection<string> folders)
    {
        List<FolderWatcher> beneath;
        lock (everyGate)
        {
            beneath = every
                .Where(_ => folders.Any(folder => Locations.IsWithin(_.Watched, folder)))
                .ToList();
        }

        foreach (var watcher in beneath)
        {
            watcher.Hold();
        }

        return new Release(beneath);
    }

    void Hold()
    {
        lock (gate)
        {
            lettingGo++;
            Refresh();
        }
    }

    void Unhold()
    {
        bool report;
        lock (gate)
        {
            lettingGo--;
            if (disposed)
            {
                return;
            }

            report = Refresh();
        }

        if (report)
        {
            changed();
        }
    }

    sealed class Release(List<FolderWatcher> watchers) : IDisposable
    {
        bool released;

        public void Dispose()
        {
            if (released)
            {
                return;
            }

            released = true;
            foreach (var watcher in watchers)
            {
                watcher.Unhold();
            }
        }
    }

    public void Dispose()
    {
        lock (everyGate)
        {
            every.Remove(this);
        }

        lock (gate)
        {
            disposed = true;
            watcher?.Dispose();
            watcher = null;
        }

        removal.Dispose();
    }
}
