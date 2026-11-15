/// <summary>
/// Undo and redo: what each kind of change does to undo and redo it, against a made-up file
/// system, and a rename and a move undone on disk.
/// </summary>
[STAThreadExecutor]
public class UndoHistoryTests
{
    // Records what it was asked to do, and does it on paper.
    sealed class FakeActions : UndoHistory.IFileActions
    {
        public List<string> Calls { get; } = [];
        public List<string> MissingFromBin { get; } = [];
        int binCounter;

        public string Rename(IntPtr owner, string path, string newName)
        {
            Calls.Add($"rename {path} to {newName}");
            return Path.Combine(Path.GetDirectoryName(path)!, newName);
        }

        public IReadOnlyList<(string From, string To)> Move(IntPtr owner, IReadOnlyList<(string From, string Folder, string Name)> items)
        {
            foreach (var item in items)
            {
                Calls.Add($"move {item.From} to {Path.Combine(item.Folder, item.Name)}");
            }

            return items.Select(_ => (_.From, Path.Combine(_.Folder, _.Name))).ToList();
        }

        public IReadOnlyList<(string Original, string InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths)
        {
            Calls.Add($"recycle {string.Join(", ", paths)}");
            return paths.Select(_ => (_, $@"C:\$Recycle.Bin\$R{++binCounter}")).ToList();
        }

        public IReadOnlyList<string> Restore(IReadOnlyList<string> inBin)
        {
            Calls.Add($"restore {string.Join(", ", inBin)}");
            return inBin.Where(MissingFromBin.Contains).ToList();
        }
    }

    [Test]
    public async Task EachKindOfChange()
    {
        var actions = new FakeActions();
        var history = new UndoHistory(actions);
        var labels = new List<string>();

        history.RecordRename(@"C:\Work\old.txt", @"C:\Work\new.txt");
        history.RecordMove([(@"C:\Work\a.txt", @"C:\Archive\a.txt")]);
        history.RecordCreated("copy", [@"C:\Work\b - Copy.txt"]);
        history.RecordDelete([(@"C:\Work\c.txt", @"C:\$Recycle.Bin\$RC")]);
        history.RecordCreated("new folder", [@"C:\Work\New folder"]);

        // Undo all five, newest first, then redo them, oldest first.
        for (var step = 0; step < 5; step++)
        {
            labels.Add(history.UndoText);
            await history.Undo(IntPtr.Zero);
        }

        for (var step = 0; step < 5; step++)
        {
            labels.Add(history.RedoText);
            await history.Redo(IntPtr.Zero);
        }

        await Verify(
            new
            {
                labels,
                actions.Calls,
                afterwards = new
                {
                    undo = history.UndoText,
                    redo = history.RedoText
                }
            });
    }

    // Something new clears what could be redone.
    [Test]
    public async Task SomethingNewClearsRedo()
    {
        var history = new UndoHistory(new FakeActions());
        history.RecordRename(@"C:\Work\a.txt", @"C:\Work\b.txt");
        await history.Undo(IntPtr.Zero);
        var canRedo = history.CanRedo;

        history.RecordCreated("new folder", [@"C:\Work\New folder"]);

        await Verify(
            new
            {
                canRedoBefore = canRedo,
                canRedoAfter = history.CanRedo,
                undo = history.UndoText
            });
    }

    // A delete whose item has gone from the Recycle Bin can't be undone, and stays undoable.
    [Test]
    public async Task EmptiedRecycleBin()
    {
        var actions = new FakeActions();
        actions.MissingFromBin.Add(@"C:\$Recycle.Bin\$RC");
        var history = new UndoHistory(actions);
        history.RecordDelete([(@"C:\Work\c.txt", @"C:\$Recycle.Bin\$RC")]);

        var failure = await history.Undo(IntPtr.Zero);

        await Verify(
            new
            {
                failure,
                undo = history.UndoText,
                canRedo = history.CanRedo
            });
    }

    // On disk: rename and move undone and redone through Windows' copy engine.
    [Test]
    public async Task RenameAndMoveOnDisk()
    {
        using var fixture = Fixture.Create();
        var history = new UndoHistory(new UndoHistory.ShellFileActions());

        File.Move(fixture.At("notes.aleph"), fixture.At("renamed.aleph"));
        history.RecordRename(fixture.At("notes.aleph"), fixture.At("renamed.aleph"));
        File.Move(fixture.At("report2.beta"), fixture.At("Archive", "report2.beta"));
        history.RecordMove([(fixture.At("report2.beta"), fixture.At("Archive", "report2.beta"))]);

        var undoMove = await history.Undo(IntPtr.Zero);
        var undoRename = await history.Undo(IntPtr.Zero);
        var undone = fixture.Tree();
        var redoRename = await history.Redo(IntPtr.Zero);
        var redoMove = await history.Redo(IntPtr.Zero);

        await Verify(
            new
            {
                failures = new[] { undoMove, undoRename, redoRename, redoMove }.Where(_ => _ != null),
                undone,
                redone = fixture.Tree()
            });
    }
}
