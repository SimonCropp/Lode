/// <summary>
/// What Ctrl+Z undoes and Ctrl+Y redoes: Lode's own renames, moves, copies, deletes to the
/// Recycle Bin and new folders, files and shortcuts, across every window, most recent first.
/// Each is reversed by Lode itself: a rename or move is done back, a copy or new item goes to
/// the Recycle Bin, a deleted item is restored from it. Redo does the change again. Doing
/// something new clears what could be redone, as in Explorer.
/// </summary>
sealed class UndoHistory(UndoHistory.IFileActions actions)
{
    /// <summary>
    /// The file system work undo and redo need. The real one goes through Windows' copy engine
    /// and Recycle Bin; tests give their own.
    /// </summary>
    public interface IFileActions
    {
        /// <summary>
        /// Renames <paramref name="path"/>, returning its new path, or null when that failed.
        /// </summary>
        string? Rename(IntPtr owner, string path, string newName);

        /// <summary>
        /// Moves each item into a folder under a name, returning the old and new path of each
        /// one moved.
        /// </summary>
        IReadOnlyList<(string From, string To)> Move(IntPtr owner, IReadOnlyList<(string From, string Folder, string Name)> items);

        /// <summary>
        /// Sends the items to the Recycle Bin, returning where each one went there.
        /// </summary>
        IReadOnlyList<(string Original, string InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths);

        /// <summary>
        /// Restores items from the Recycle Bin, returning those it could not find.
        /// </summary>
        IReadOnlyList<string> Restore(IReadOnlyList<string> inBin);
    }

    /// <summary>
    /// The history every window shares. Tests put one in that records nothing, so what they do
    /// to files elsewhere doesn't show in each other's menus.
    /// </summary>
    public static UndoHistory Instance { get; internal set; } = new(new ShellFileActions());

    /// <summary>
    /// Whether changes are recorded. Off in tests.
    /// </summary>
    public bool Records { get; init; } = true;

    const int maximumEntries = 50;

    readonly Lock gate = new();
    readonly List<Entry> undoable = [];
    readonly List<Entry> redoable = [];
    bool busy;

    /// <summary>
    /// Raised, on whichever thread changed it, when what can be undone or redone changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// A change that can be undone and redone, named for the menus: "rename", "move" and so on.
    /// <see cref="Undo"/> and <see cref="Redo"/> return a reason when they could not.
    /// </summary>
    public abstract class Entry(string name)
    {
        public string Name { get; } = name;

        public abstract string? Undo(IFileActions actions, IntPtr owner);

        public abstract string? Redo(IFileActions actions, IntPtr owner);
    }

    public string? UndoName
    {
        get
        {
            lock (gate)
            {
                return undoable.LastOrDefault()?.Name;
            }
        }
    }

    public string? RedoName
    {
        get
        {
            lock (gate)
            {
                return redoable.LastOrDefault()?.Name;
            }
        }
    }

    public bool CanUndo => UndoName != null;

    public bool CanRedo => RedoName != null;

    /// <summary>
    /// "Undo rename", or "Undo" with nothing to undo.
    /// </summary>
    public string UndoText => Label("Undo", UndoName);

    public string RedoText => Label("Redo", RedoName);

    static string Label(string verb, string? name)
    {
        if (name == null)
        {
            return verb;
        }

        return $"{verb} {name}";
    }

    public void Record(Entry entry)
    {
        if (!Records)
        {
            return;
        }

        lock (gate)
        {
            undoable.Add(entry);
            if (undoable.Count > maximumEntries)
            {
                undoable.RemoveAt(0);
            }

            redoable.Clear();
        }

        Changed?.Invoke();
    }

    public void RecordRename(string oldPath, string newPath) =>
        Record(new Renamed(oldPath, newPath));

    public void RecordMove(IReadOnlyList<(string From, string To)> moved)
    {
        if (moved.Count > 0)
        {
            Record(new Moved(moved));
        }
    }

    /// <summary>
    /// Items that did not exist before: copies, or a new folder, file or shortcut.
    /// </summary>
    public void RecordCreated(string name, IReadOnlyList<string> created)
    {
        if (created.Count > 0)
        {
            Record(new Created(name, created));
        }
    }

    public void RecordDelete(IReadOnlyList<(string Original, string InBin)> deleted)
    {
        if (deleted.Count > 0)
        {
            Record(new Deleted(deleted));
        }
    }

    /// <summary>
    /// Undoes the most recent change. Returns why it could not, or null when it did or there
    /// was nothing to undo.
    /// </summary>
    public Task<string?> Undo(IntPtr owner) =>
        Step(undoable, redoable, (entry, actionsToUse) => entry.Undo(actionsToUse, owner));

    public Task<string?> Redo(IntPtr owner) =>
        Step(redoable, undoable, (entry, actionsToUse) => entry.Redo(actionsToUse, owner));

    // Takes the entry off one list for the work, which can show Windows' progress and conflict
    // dialogs, so runs on a thread of its own. It goes on the other list when done, or back
    // where it was when it could not be done.
    async Task<string?> Step(List<Entry> from, List<Entry> to, Func<Entry, IFileActions, string?> work)
    {
        Entry entry;
        lock (gate)
        {
            if (busy ||
                from.Count == 0)
            {
                return null;
            }

            busy = true;
            entry = from[^1];
            from.RemoveAt(from.Count - 1);
        }

        string? failure;
        try
        {
            failure = await FileOperations.OnStaThread(() => work(entry, actions));
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }

        lock (gate)
        {
            busy = false;
            if (failure == null)
            {
                to.Add(entry);
            }
            else
            {
                from.Add(entry);
            }
        }

        Changed?.Invoke();
        return failure;
    }

    static string NameOf(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    static string ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar))!;

    sealed class Renamed(string oldPath, string newPath) : Entry("rename")
    {
        public override string? Undo(IFileActions actions, IntPtr owner) =>
            Outcome(actions.Rename(owner, newPath, NameOf(oldPath)), NameOf(newPath));

        public override string? Redo(IFileActions actions, IntPtr owner) =>
            Outcome(actions.Rename(owner, oldPath, NameOf(newPath)), NameOf(oldPath));

        static string? Outcome(string? renamed, string name)
        {
            if (renamed != null)
            {
                return null;
            }

            return $"Lode couldn't rename '{name}' back.";
        }
    }

    sealed class Moved(IReadOnlyList<(string From, string To)> initial) : Entry("move")
    {
        IReadOnlyList<(string From, string To)> moved = initial;

        public override string? Undo(IFileActions actions, IntPtr owner)
        {
            var back = actions.Move(owner, moved.Select(_ => (_.To, ParentOf(_.From), NameOf(_.From))).ToList());
            return Outcome(back.Count);
        }

        public override string? Redo(IFileActions actions, IntPtr owner)
        {
            var again = actions.Move(owner, moved.Select(_ => (_.From, ParentOf(_.To), NameOf(_.To))).ToList());
            if (again.Count > 0)
            {
                moved = again;
            }

            return Outcome(again.Count);
        }

        string? Outcome(int done)
        {
            if (done == moved.Count)
            {
                return null;
            }

            return $"Lode moved {done} of {moved.Count} items back.";
        }
    }

    // Copies and new items: undone to the Recycle Bin, redone from it.
    sealed class Created(string name, IReadOnlyList<string> created) : Entry(name)
    {
        IReadOnlyList<string> inBin = [];

        public override string? Undo(IFileActions actions, IntPtr owner)
        {
            var recycled = actions.Recycle(owner, created);
            inBin = recycled.Select(_ => _.InBin).ToList();
            if (recycled.Count == created.Count)
            {
                return null;
            }

            return $"Lode sent {recycled.Count} of {created.Count} items to the Recycle Bin.";
        }

        public override string? Redo(IFileActions actions, IntPtr owner) =>
            RestoreOutcome(actions.Restore(inBin), inBin.Count);
    }

    sealed class Deleted(IReadOnlyList<(string Original, string InBin)> initial) : Entry("delete")
    {
        IReadOnlyList<(string Original, string InBin)> deleted = initial;

        public override string? Undo(IFileActions actions, IntPtr owner) =>
            RestoreOutcome(actions.Restore(deleted.Select(_ => _.InBin).ToList()), deleted.Count);

        public override string? Redo(IFileActions actions, IntPtr owner)
        {
            var again = actions.Recycle(owner, deleted.Select(_ => _.Original).ToList());
            if (again.Count == 0)
            {
                return "Lode couldn't delete them again.";
            }

            deleted = again;
            return null;
        }
    }

    static string? RestoreOutcome(IReadOnlyList<string> missing, int count)
    {
        if (missing.Count == 0)
        {
            return null;
        }

        return $"{missing.Count} of {count} items are no longer in the Recycle Bin, so can't be restored.";
    }

    /// <summary>
    /// The real file system: Windows' copy engine, without adding to its own undo record, and
    /// the Recycle Bin.
    /// </summary>
    internal sealed class ShellFileActions : IFileActions
    {
        const uint recycle = ShellFileOperation.AllowUndo | ShellFileOperation.RecycleOnDelete;

        public string? Rename(IntPtr owner, string path, string newName)
        {
            var outcome = ShellFileOperation.Run(owner, ShellFileOperation.Kind.Rename, [path], newName, 0);
            if (outcome.Changes is [{ Result: { } renamed }, ..])
            {
                return renamed;
            }

            return null;
        }

        public IReadOnlyList<(string From, string To)> Move(IntPtr owner, IReadOnlyList<(string From, string Folder, string Name)> items)
        {
            var outcome = ShellFileOperation.Run(
                owner,
                ShellFileOperation.Kind.Move,
                items.Select(_ => new ShellFileOperation.Item(_.From, _.Folder, _.Name)).ToList(),
                0);
            return Pairs(outcome);
        }

        public IReadOnlyList<(string Original, string InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths) =>
            Pairs(ShellFileOperation.Run(owner, ShellFileOperation.Kind.Delete, paths, null, recycle));

        public IReadOnlyList<string> Restore(IReadOnlyList<string> inBin) =>
            RecycleBin.Restore(inBin);

        static List<(string, string)> Pairs(ShellFileOperation.Outcome outcome) =>
            outcome.Changes
                .Where(_ => _.Result != null)
                .Select(_ => (_.Source, _.Result!))
                .ToList();
    }
}
