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
        /// Sends the items to the Recycle Bin, returning each one deleted, with where it went
        /// there, or null for one deleted outright: on a drive with no Recycle Bin, a USB stick
        /// or a share, Windows asks, and deletes it for good.
        /// </summary>
        IReadOnlyList<(string Original, string? InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths);

        /// <summary>
        /// Restores items from the Recycle Bin, returning those not restored: gone from it, or
        /// still in it, skipped at the shell's prompt.
        /// </summary>
        IReadOnlyList<string> Restore(IReadOnlyList<string> inBin);

        /// <summary>
        /// Whether a file or folder is still there.
        /// </summary>
        bool Exists(string path);

        /// <summary>
        /// Makes a folder, and any it is in that are not there.
        /// </summary>
        void CreateFolder(string path);

        /// <summary>
        /// Deletes a folder where it is there and empty.
        /// </summary>
        void DeleteIfEmpty(string path);
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

    // How many changes have been recorded, so an undo or redo knows of those made meanwhile.
    long recorded;

    /// <summary>
    /// Raised, on whichever thread changed it, when what can be undone or redone changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// How undoing or redoing a change went, which decides what becomes of it.
    /// </summary>
    public enum Result
    {
        /// <summary>
        /// Done, all of it or some. The change is then what was done, and can be done the other
        /// way.
        /// </summary>
        Done,

        /// <summary>
        /// None of it done, though it still could be: Windows' dialog was cancelled, say. It
        /// stays where it was, to be tried again.
        /// </summary>
        Waiting,

        /// <summary>
        /// None of it done, and none of it can be: what it was done to has gone since. It is let
        /// go. Left where it was, it would fail the same way each time, and nothing done before
        /// it could be reached.
        /// </summary>
        Gone,

        /// <summary>
        /// Done, but it can't be done the other way: what it sent to the Recycle Bin was deleted
        /// outright instead, so nothing is there to bring back. It is let go.
        /// </summary>
        DoneForGood
    }

    /// <param name="Failure">Why not all of it was done, when it was not.</param>
    public readonly record struct Outcome(Result Result, string? Failure = null);

    /// <summary>
    /// A change that can be undone and redone, named for the menus: "rename", "move" and so on.
    /// </summary>
    public abstract class Entry(string name)
    {
        public string Name { get; } = name;

        // Whether the last try at it did none of it, though it still could be done.
        internal bool Failed { get; set; }

        public abstract Outcome Undo(IFileActions actions, IntPtr owner);

        public abstract Outcome Redo(IFileActions actions, IntPtr owner);
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
            recorded++;
        }

        Changed?.Invoke();
    }

    public void RecordRename(string oldPath, string newPath) =>
        Record(new Renamed(oldPath, newPath));

    /// <param name="emptied">
    /// Folders merged into one of the same name already there: Windows moves what is in each
    /// item by item, in <paramref name="moved"/>, and then deletes it. Undo makes it again to
    /// move those back into, and redo deletes it again once it is empty.
    /// </param>
    /// <param name="name">"move", or "rename" for a rename that merged.</param>
    public void RecordMove(IReadOnlyList<(string From, string To)> moved, IReadOnlyList<string>? emptied = null, string name = "move")
    {
        if (moved.Count > 0)
        {
            Record(new Moved(moved, emptied ?? [], name));
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
    // dialogs, so runs on a thread of its own. It goes on the other list when done, back where
    // it was when it could not be done but still could be, and on neither when it never can be.
    // A change recorded meanwhile cleared redo, which nothing then goes back on: an entry undone
    // is not to be redone after it, and one that was to be redone is not any more. One not
    // undone, or redone, goes before the newer changes, which are undone first.
    async Task<string?> Step(List<Entry> from, List<Entry> to, Func<Entry, IFileActions, Outcome> work)
    {
        Entry entry;
        long recordedBefore;
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
            recordedBefore = recorded;
        }

        Outcome outcome;
        try
        {
            outcome = await FileOperations.OnStaThread(() => work(entry, actions));
        }
        catch (Exception exception)
        {
            // Whatever stopped it may stop it every time, so it is not left in the way.
            outcome = new(Result.Gone, exception.Message);
        }

        // Not done a second time running, access denied say, it may never be, and would keep
        // everything before it from being reached. It is let go.
        var failedBefore = entry.Failed;
        entry.Failed = outcome.Result == Result.Waiting;
        if (failedBefore &&
            entry.Failed)
        {
            outcome = new(Result.Gone, $"{outcome.Failure} Not done twice running, it is let go.");
        }

        lock (gate)
        {
            busy = false;
            var newer = recorded - recordedBefore;
            var list = to;
            if (outcome.Result == Result.Waiting)
            {
                list = from;
            }

            if (outcome.Result is Result.Done or Result.Waiting)
            {
                if (newer == 0)
                {
                    list.Add(entry);
                }
                else if (ReferenceEquals(list, undoable))
                {
                    undoable.Insert(undoable.Count - (int)Math.Min(newer, undoable.Count), entry);
                    if (undoable.Count > maximumEntries)
                    {
                        undoable.RemoveAt(0);
                    }
                }
            }
        }

        Changed?.Invoke();
        return outcome.Failure;
    }

    static string NameOf(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    static string ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar))!;

    sealed class Renamed(string oldPath, string newPath) : Entry("rename")
    {
        public override Outcome Undo(IFileActions actions, IntPtr owner)
        {
            var outcome = Rename(actions, owner, newPath, NameOf(oldPath), "back", out var renamed);
            oldPath = renamed ?? oldPath;
            return outcome;
        }

        public override Outcome Redo(IFileActions actions, IntPtr owner)
        {
            var outcome = Rename(actions, owner, oldPath, NameOf(newPath), "again", out var renamed);
            newPath = renamed ?? newPath;
            return outcome;
        }

        // Where Windows put it is where it is renamed from next time: that is not always the
        // name asked for, which something else may have taken since.
        static Outcome Rename(IFileActions actions, IntPtr owner, string path, string newName, string direction, out string? renamed)
        {
            renamed = null;
            var name = NameOf(path);
            if (!actions.Exists(path))
            {
                return new(Result.Gone, $"'{name}' is no longer there, so can't be renamed {direction}.");
            }

            renamed = actions.Rename(owner, path, newName);
            if (renamed == null)
            {
                return new(Result.Waiting, $"Lode couldn't rename '{name}' {direction}.");
            }

            return new(Result.Done);
        }
    }

    sealed class Moved(IReadOnlyList<(string From, string To)> initial, IReadOnlyList<string> emptied, string name) : Entry(name)
    {
        IReadOnlyList<(string From, string To)> moved = initial;

        public override Outcome Undo(IFileActions actions, IntPtr owner)
        {
            // Made again for what was in them to go back into, empty folders included.
            foreach (var folder in emptied)
            {
                if (!actions.Exists(folder))
                {
                    actions.CreateFolder(folder);
                }
            }

            var outcome = Move(actions, owner, moved.Select(_ => (_.To, _.From)).ToList(), "back", out var back);
            if (outcome.Result == Result.Done)
            {
                moved = back.Select(_ => (_.To, _.From)).ToList();
            }

            return outcome;
        }

        public override Outcome Redo(IFileActions actions, IntPtr owner)
        {
            var outcome = Move(actions, owner, moved, "again", out var again);
            if (outcome.Result == Result.Done)
            {
                moved = again;
                // As Windows did when it merged them. The deepest first, so one inside another
                // is gone by the time the other is looked at.
                foreach (var folder in emptied.OrderByDescending(_ => _.Length))
                {
                    actions.DeleteIfEmpty(folder);
                }
            }

            return outcome;
        }

        // Moves each item to where it is wanted, giving those moved, each with where Windows
        // put it. One that has gone, or whose folder to go back into has, is left out: asked to
        // move something that is not there, Windows moves none of them.
        static Outcome Move(IFileActions actions, IntPtr owner, IReadOnlyList<(string From, string To)> items, string direction, out IReadOnlyList<(string From, string To)> done)
        {
            done = [];
            var there = items
                .Where(_ => actions.Exists(_.From) &&
                            actions.Exists(ParentOf(_.To)))
                .ToList();
            if (there.Count == 0)
            {
                return new(Result.Gone, $"What was moved is no longer there, or the folder it was in is not, so it can't be moved {direction}.");
            }

            done = actions.Move(owner, there.Select(_ => (_.From, ParentOf(_.To), NameOf(_.To))).ToList());
            return Counted(done.Count, items.Count, $"Lode moved {done.Count} of {items.Count} items {direction}.");
        }
    }

    // Copies and new items: undone to the Recycle Bin, redone from it.
    sealed class Created(string name, IReadOnlyList<string> initial) : Entry(name)
    {
        IReadOnlyList<string> created = initial;
        IReadOnlyList<(string Original, string InBin)> binned = [];

        public override Outcome Undo(IFileActions actions, IntPtr owner)
        {
            var outcome = Recycle(actions, owner, created, out var recycled);
            if (outcome.Result == Result.Done)
            {
                binned = recycled;
            }

            return ForGoodWhenNoneBinned(outcome, recycled);
        }

        public override Outcome Redo(IFileActions actions, IntPtr owner)
        {
            var outcome = Restore(actions, binned, out var restored);
            if (outcome.Result == Result.Done)
            {
                created = restored.Select(_ => _.Original).ToList();
            }

            return outcome;
        }
    }

    sealed class Deleted(IReadOnlyList<(string Original, string InBin)> initial) : Entry("delete")
    {
        IReadOnlyList<(string Original, string InBin)> deleted = initial;

        public override Outcome Undo(IFileActions actions, IntPtr owner)
        {
            var outcome = Restore(actions, deleted, out var restored);
            if (outcome.Result == Result.Done)
            {
                deleted = restored;
            }

            return outcome;
        }

        public override Outcome Redo(IFileActions actions, IntPtr owner)
        {
            var outcome = Recycle(actions, owner, deleted.Select(_ => _.Original).ToList(), out var again);
            if (outcome.Result == Result.Done)
            {
                deleted = again;
            }

            return ForGoodWhenNoneBinned(outcome, again);
        }
    }

    // Sends the items to the Recycle Bin, giving those sent, each with where it went there. One
    // that has gone is left out: asked to delete something that is not there, Windows deletes
    // none of them. One deleted outright, on a drive with no Recycle Bin, counts as done, as it
    // has gone as asked, but is not given, as nothing can bring it back.
    static Outcome Recycle(IFileActions actions, IntPtr owner, IReadOnlyList<string> paths, out IReadOnlyList<(string Original, string InBin)> recycled)
    {
        recycled = [];
        var there = paths
            .Where(actions.Exists)
            .ToList();
        if (there.Count == 0)
        {
            return new(Result.Gone, $"{paths.Count} of {paths.Count} items are no longer there, so can't be sent to the Recycle Bin.");
        }

        var deleted = actions.Recycle(owner, there);
        recycled = deleted
            .Where(_ => _.InBin != null)
            .Select(_ => (_.Original, _.InBin!))
            .ToList();
        return Counted(deleted.Count, paths.Count, $"Lode deleted {deleted.Count} of {paths.Count} items.");
    }

    // Done, with nothing in the Recycle Bin to do it the other way from: every item was deleted
    // outright. Left to be redone, it would do nothing.
    static Outcome ForGoodWhenNoneBinned(Outcome outcome, IReadOnlyList<(string Original, string InBin)> binned)
    {
        if (outcome.Result == Result.Done &&
            binned.Count == 0)
        {
            return outcome with
            {
                Result = Result.DoneForGood
            };
        }

        return outcome;
    }

    // Restores the items from the Recycle Bin, giving those restored. One that has left it,
    // emptied since say, never can be. One still in it, skipped at the shell's prompt for
    // something of the same name where it goes back to, still could be.
    static Outcome Restore(IFileActions actions, IReadOnlyList<(string Original, string InBin)> binned, out IReadOnlyList<(string Original, string InBin)> restored)
    {
        var notRestored = actions.Restore(binned.Select(_ => _.InBin).ToList());
        restored = binned
            .Where(_ => !notRestored.Contains(_.InBin, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (notRestored.Count == 0)
        {
            return new(Result.Done);
        }

        var missing = notRestored.Count(_ => !actions.Exists(_));
        if (missing == 0)
        {
            return Counted(restored.Count, binned.Count, $"Lode restored {restored.Count} of {binned.Count} items.");
        }

        var failure = $"{missing} of {binned.Count} items are no longer in the Recycle Bin, so can't be restored.";
        if (restored.Count > 0)
        {
            return new(Result.Done, failure);
        }

        if (missing < notRestored.Count)
        {
            return new(Result.Waiting, failure);
        }

        return new(Result.Gone, failure);
    }

    // How it went for items that are still there: done when any were, with the failure to say
    // when not all were, and otherwise to be tried again.
    static Outcome Counted(int done, int count, string failure)
    {
        if (done == count)
        {
            return new(Result.Done);
        }

        if (done == 0)
        {
            return new(Result.Waiting, failure);
        }

        return new(Result.Done, failure);
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

        // Every item deleted: one with no place in the Recycle Bin was deleted outright.
        public IReadOnlyList<(string Original, string? InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths) =>
            ShellFileOperation.Run(owner, ShellFileOperation.Kind.Delete, paths, null, recycle)
                .Changes
                .Select(_ => (_.Source, _.Result))
                .ToList();

        public IReadOnlyList<string> Restore(IReadOnlyList<string> inBin) =>
            RecycleBin.Restore(inBin);

        public bool Exists(string path) =>
            Path.Exists(path);

        public void CreateFolder(string path) =>
            Directory.CreateDirectory(path);

        public void DeleteIfEmpty(string path)
        {
            try
            {
                if (Directory.Exists(path) &&
                    !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left there: an empty folder does no harm, and the move itself was done.
            }
        }

        static List<(string, string)> Pairs(ShellFileOperation.Outcome outcome) =>
            outcome.Changes
                .Where(_ => _.Result != null)
                .Select(_ => (_.Source, _.Result!))
                .ToList();
    }
}
