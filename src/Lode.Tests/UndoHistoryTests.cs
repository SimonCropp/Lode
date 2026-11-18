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

        // No longer there: deleted since, behind Lode's back.
        public List<string> Gone { get; } = [];

        // Still there, but left as they are when asked: Windows' dialog cancelled, say.
        public List<string> Refused { get; } = [];
        int binCounter;

        public string? Rename(IntPtr owner, string path, string newName)
        {
            Calls.Add($"rename {path} to {newName}");
            if (Refused.Contains(path))
            {
                return null;
            }

            return Path.Combine(Path.GetDirectoryName(path)!, newName);
        }

        public IReadOnlyList<(string From, string To)> Move(IntPtr owner, IReadOnlyList<(string From, string Folder, string Name)> items)
        {
            foreach (var item in items)
            {
                Calls.Add($"move {item.From} to {Path.Combine(item.Folder, item.Name)}");
            }

            return items
                .Where(_ => !Refused.Contains(_.From))
                .Select(_ => (_.From, Path.Combine(_.Folder, _.Name)))
                .ToList();
        }

        public IReadOnlyList<(string Original, string InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths)
        {
            Calls.Add($"recycle {string.Join(", ", paths)}");
            return paths
                .Where(_ => !Refused.Contains(_))
                .Select(_ => (_, $@"C:\$Recycle.Bin\$R{++binCounter}"))
                .ToList();
        }

        public IReadOnlyList<string> Restore(IReadOnlyList<string> inBin)
        {
            Calls.Add($"restore {string.Join(", ", inBin)}");
            return inBin.Where(MissingFromBin.Contains).ToList();
        }

        public bool Exists(string path) =>
            !Gone.Contains(path);

        public void CreateFolder(string path)
        {
            Calls.Add($"create {path}");
            Gone.Remove(path);
        }

        public void DeleteIfEmpty(string path) =>
            Calls.Add($"delete if empty {path}");
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

    // A delete whose item has gone from the Recycle Bin can't be undone, and never can be. It
    // is let go, so the rename before it can still be undone.
    [Test]
    public async Task EmptiedRecycleBin()
    {
        var actions = new FakeActions();
        actions.MissingFromBin.Add(@"C:\$Recycle.Bin\$RC");
        var history = new UndoHistory(actions);
        history.RecordRename(@"C:\Work\old.txt", @"C:\Work\new.txt");
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

    // Nor can a change to something that has gone since: a new folder deleted behind Lode's
    // back. Left to be tried again it would fail the same way each time, in the way of what
    // was done before it. Windows is not asked to delete what is not there.
    [Test]
    public async Task AChangeToWhatHasGoneIsLetGo()
    {
        var actions = new FakeActions();
        var history = new UndoHistory(actions);
        history.RecordRename(@"C:\Work\old.txt", @"C:\Work\new.txt");
        history.RecordCreated("new folder", [@"C:\Work\New folder"]);
        actions.Gone.Add(@"C:\Work\New folder");

        var failure = await history.Undo(IntPtr.Zero);

        await Assert.That(failure).IsEqualTo("1 of 1 items are no longer there, so can't be sent to the Recycle Bin.");
        await Assert.That(actions.Calls).IsEmpty();
        await Assert.That(history.UndoText).IsEqualTo("Undo rename");
        await Assert.That(history.CanRedo).IsFalse();
    }

    // One that was not undone, though what it was done to is still there, stays to be tried
    // again: Windows' dialog was cancelled, say.
    [Test]
    public async Task AChangeNotUndoneStaysToBeTriedAgain()
    {
        var actions = new FakeActions();
        var history = new UndoHistory(actions);
        history.RecordMove([(@"C:\Work\a.txt", @"C:\Archive\a.txt")]);
        actions.Refused.Add(@"C:\Archive\a.txt");

        var failure = await history.Undo(IntPtr.Zero);
        var afterFailing = (history.UndoText, history.CanRedo);
        actions.Refused.Clear();
        var retried = await history.Undo(IntPtr.Zero);

        await Assert.That(failure).IsEqualTo("Lode moved 0 of 1 items back.");
        await Assert.That(afterFailing).IsEqualTo(("Undo move", false));
        await Assert.That(retried).IsNull();
        await Assert.That(history.RedoText).IsEqualTo("Redo move");
    }

    // Undone in part, a change becomes what was undone, which can be redone. The rest is let
    // go: tried again whole, it would fail for the part already back where it was.
    [Test]
    public async Task AChangeUndoneInPartIsWhatWasUndone()
    {
        var actions = new FakeActions();
        var history = new UndoHistory(actions);
        history.RecordMove(
        [
            (@"C:\Work\a.txt", @"C:\Archive\a.txt"),
            (@"C:\Work\b.txt", @"C:\Archive\b.txt"),
            (@"C:\Work\c.txt", @"C:\Archive\c.txt")
        ]);
        actions.Gone.Add(@"C:\Archive\b.txt");
        actions.Refused.Add(@"C:\Archive\c.txt");

        var undone = await history.Undo(IntPtr.Zero);
        var canUndo = history.CanUndo;
        actions.Calls.Clear();
        var redone = await history.Redo(IntPtr.Zero);

        await Assert.That(undone).IsEqualTo("Lode moved 1 of 3 items back.");
        await Assert.That(canUndo).IsFalse();
        await Assert.That(redone).IsNull();
        await Assert.That(actions.Calls).IsEquivalentTo([@"move C:\Work\a.txt to C:\Archive\a.txt"]);
    }

    // Redone in part, it says so, as it does when undone in part.
    [Test]
    public async Task AChangeRedoneInPartSaysSo()
    {
        var actions = new FakeActions();
        var history = new UndoHistory(actions);
        history.RecordMove(
        [
            (@"C:\Work\a.txt", @"C:\Archive\a.txt"),
            (@"C:\Work\b.txt", @"C:\Archive\b.txt")
        ]);
        await history.Undo(IntPtr.Zero);
        actions.Refused.Add(@"C:\Work\b.txt");

        var redone = await history.Redo(IntPtr.Zero);

        await Assert.That(redone).IsEqualTo("Lode moved 1 of 2 items again.");
        await Assert.That(history.UndoText).IsEqualTo("Undo move");
        await Assert.That(history.CanRedo).IsFalse();
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

    // On disk: a folder moved into one of the same name already there. Windows merges the two,
    // moving what is in the one item by item, then deletes it. Undo moves only those back, into
    // the folder made again, empty folders included, and leaves what was there before. Redo
    // merges them again.
    [Test]
    public async Task MergingMoveOnDisk()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Archive", "Source", "Deep"));
        await File.WriteAllTextAsync(fixture.At("Archive", "Source", "existing.aleph"), "there before");
        await File.WriteAllTextAsync(fixture.At("Archive", "Source", "Deep", "existing.aleph"), "there before");
        Directory.CreateDirectory(fixture.At("Source", "Empty"));
        Directory.CreateDirectory(fixture.At("Archive", "Source", "Empty"));
        var history = new UndoHistory(new UndoHistory.ShellFileActions());

        var outcome = ShellFileOperation.Run(
            IntPtr.Zero,
            ShellFileOperation.Kind.Move,
            [fixture.At("Source")],
            fixture.At("Archive"),
            ShellFileOperation.NoConfirmation);
        history.RecordMove(
            outcome.Changes.Select(_ => (_.Source, _.Result!)).ToList(),
            outcome.Merged.Select(_ => _.Source).ToList());
        var moved = fixture.Tree();
        var undo = await history.Undo(IntPtr.Zero);
        var undone = fixture.Tree();
        var redo = await history.Redo(IntPtr.Zero);

        await Verify(
            new
            {
                failures = new[] { undo, redo }.Where(_ => _ != null),
                moved,
                undone,
                redone = fixture.Tree()
            });
    }

    // On disk: a new folder deleted since. Windows' copy engine refuses everything it is asked
    // when one item is not there, so it is not asked, and the rename before is still undone.
    [Test]
    public async Task ANewFolderDeletedSinceOnDisk()
    {
        using var fixture = Fixture.Create();
        var history = new UndoHistory(new UndoHistory.ShellFileActions());
        File.Move(fixture.At("notes.aleph"), fixture.At("renamed.aleph"));
        history.RecordRename(fixture.At("notes.aleph"), fixture.At("renamed.aleph"));
        Directory.CreateDirectory(fixture.At("New folder"));
        history.RecordCreated("new folder", [fixture.At("New folder")]);
        Directory.Delete(fixture.At("New folder"));

        var undoFolder = await history.Undo(IntPtr.Zero);
        var undoRename = await history.Undo(IntPtr.Zero);

        await Assert.That(undoFolder).IsEqualTo("1 of 1 items are no longer there, so can't be sent to the Recycle Bin.");
        await Assert.That(undoRename).IsNull();
        await Assert.That(File.Exists(fixture.At("notes.aleph"))).IsTrue();
        await Assert.That(history.CanUndo).IsFalse();
    }
}
