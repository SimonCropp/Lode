using System.Collections.Concurrent;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

/// <summary>
/// Files dragged that are not on disk, as attachments from Outlook and photos from a phone are,
/// given as FileGroupDescriptorW and FileContents: what a drop writes, and where.
/// </summary>
[WindowThread]
public class VirtualFilesTests
{
    static readonly DateTime created = new(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    static readonly DateTime written = new(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    static DragEventArgs Drag(VirtualSource source, DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move, int keyState = 0) =>
        new(new DataObject(source), keyState, 0, 0, allowed, DragDropEffects.None);

    // Dropped on a folder, each file is written there with its bytes and times: from a block of
    // memory, no more of it than the descriptor says, a stream, read to its end, more than a
    // buffer's worth, and a compound file, as Outlook gives a message. A folder comes with what
    // is in it, one inside it made though it isn't named on its own, and a file named as one
    // before it, but for case, is numbered. Those named to go outside the folder, or as a
    // device, are left out, and said to be, and Lode's own folder they were written to goes.
    [Test]
    public async Task DroppedFilesAreWrittenWithTheirContentsAndTimes()
    {
        using var fixture = Fixture.Create();
        var photo = RandomNumberGenerator.GetBytes(3_000_000);
        var source = new VirtualSource(
            new("notes.txt", "hello"u8.ToArray(), Created: created, Written: written),
            new("Photos", Created: created, Written: written),
            new(@"Photos\Trip\beach.jpg", photo, VirtualSource.Form.Stream, created, written),
            new(@"..\escape.txt", "out"u8.ToArray()),
            new("CON.txt", "device"u8.ToArray()),
            new("NOTES.txt", "again"u8.ToArray(), Written: written),
            new("message.msg", "body"u8.ToArray(), VirtualSource.Form.Storage));
        var destination = fixture.At("Archive");

        var staging = VirtualFiles.Stage(new DataObject(source))!;
        var (placed, notCopied) = await FileOperations.CopyVirtual(IntPtr.Zero, staging, destination);

        await Assert.That(placed.Select(fixture.Relative)).IsEquivalentTo(["Projects/Archive/notes.txt", "Projects/Archive/Photos", "Projects/Archive/NOTES (2).txt", "Projects/Archive/message.msg"]);
        await Assert.That(await File.ReadAllTextAsync(fixture.At("Archive", "notes.txt"))).IsEqualTo("hello");
        await Assert.That(await File.ReadAllTextAsync(fixture.At("Archive", "NOTES (2).txt"))).IsEqualTo("again");
        await Assert.That((await File.ReadAllBytesAsync(fixture.At("Archive", "Photos", "Trip", "beach.jpg"))).SequenceEqual(photo)).IsTrue();
        await Assert.That(Body(fixture.At("Archive", "message.msg"))).IsEqualTo("body");
        await Assert.That((File.GetCreationTimeUtc(fixture.At("Archive", "notes.txt")), File.GetLastWriteTimeUtc(fixture.At("Archive", "notes.txt")))).IsEqualTo((created, written));
        await Assert.That((File.GetCreationTimeUtc(fixture.At("Archive", "Photos", "Trip", "beach.jpg")), File.GetLastWriteTimeUtc(fixture.At("Archive", "Photos", "Trip", "beach.jpg")))).IsEqualTo((created, written));
        await Assert.That((Directory.GetCreationTimeUtc(fixture.At("Archive", "Photos")), Directory.GetLastWriteTimeUtc(fixture.At("Archive", "Photos")))).IsEqualTo((created, written));
        await Assert.That(File.GetLastWriteTimeUtc(fixture.At("Archive", "NOTES (2).txt"))).IsEqualTo(written);
        await Assert.That(File.Exists(fixture.At("escape.txt"))).IsFalse();
        await Assert.That(Directory.EnumerateFileSystemEntries(fixture.At("Archive")).Select(fixture.Relative)).IsEquivalentTo(["Projects/Archive/notes.txt", "Projects/Archive/Photos", "Projects/Archive/NOTES (2).txt", "Projects/Archive/message.msg"]);
        await Assert.That(notCopied).IsEqualTo(
            """
            Lode left out 2 items named to go outside the folder, or with names Windows can't have or keeps for devices:

              ..\escape.txt
              CON.txt
            """.ReplaceLineEndings("\n"));
        await Assert.That(Directory.Exists((await staging).Folder)).IsFalse();
    }

    // What one drop made is one change, a copy, which undo sends to the Recycle Bin: what it put
    // at the top of the folder, the folder whole.
    [Test]
    public async Task UndoSendsWhatWasMadeToTheRecycleBin()
    {
        using var fixture = Fixture.Create();
        var source = new VirtualSource(
            new("notes.txt", "hello"u8.ToArray()),
            new(@"Photos\beach.jpg", "sand"u8.ToArray(), VirtualSource.Form.Stream));
        var recycling = new Recycling();
        var history = UndoHistory.Instance;
        UndoHistory.Instance = new(recycling);
        string? undoText;
        try
        {
            await FileOperations.CopyVirtual(IntPtr.Zero, VirtualFiles.Stage(new DataObject(source))!, fixture.At("Archive"));
            undoText = UndoHistory.Instance.UndoText;

            // Tests elsewhere may record in it meanwhile, and are undone on paper too.
            while (UndoHistory.Instance.CanUndo)
            {
                await UndoHistory.Instance.Undo(IntPtr.Zero);
            }
        }
        finally
        {
            UndoHistory.Instance = history;
        }

        var undone = recycling.Recycled
            .Where(_ => _.All(path => Locations.IsWithin(path, fixture.Root)))
            .ToList();
        await Assert.That(undoText).IsEqualTo("Undo copy");
        await Assert.That(undone.Count).IsEqualTo(1);
        await Assert.That(undone[0].Select(fixture.Relative)).IsEquivalentTo(["Projects/Archive/notes.txt", "Projects/Archive/Photos"]);
    }

    // Undo's work done on paper: what it sends to the Recycle Bin.
    sealed class Recycling : UndoHistory.IFileActions
    {
        public ConcurrentQueue<IReadOnlyList<string>> Recycled { get; } = [];

        public string? Rename(IntPtr owner, string path, string newName) =>
            null;

        public IReadOnlyList<(string From, string To)> Move(IntPtr owner, IReadOnlyList<(string From, string Folder, string Name)> items) =>
            [];

        public IReadOnlyList<(string Original, string? InBin)> Recycle(IntPtr owner, IReadOnlyList<string> paths)
        {
            Recycled.Enqueue(paths);
            return paths
                .Select(_ => (_, (string?)null))
                .ToList();
        }

        public IReadOnlyCollection<string> Restore(IntPtr owner, IReadOnlyList<string> inBin) =>
            inBin;

        public bool Exists(string path) =>
            Path.Exists(path);

        public void CreateFolder(string path)
        {
        }

        public void DeleteIfEmpty(string path)
        {
        }
    }

    // Dragged over a folder, such files are copied whatever the keys held: a move would tell the
    // source to delete what it gave. Nothing is offered where the source doesn't allow a copy,
    // or there is nowhere to drop.
    [Test]
    public async Task TheyCanOnlyBeCopied()
    {
        using var fixture = Fixture.Create();
        var source = new VirtualSource(new VirtualSource.Entry("notes.txt", "hello"u8.ToArray()));
        const int shift = 4;

        await Assert.That(FileOperations.GetDropEffect(Drag(source), fixture.Root)).IsEqualTo(DragDropEffects.Copy);
        await Assert.That(FileOperations.GetDropEffect(Drag(source, keyState: shift), fixture.Root)).IsEqualTo(DragDropEffects.Copy);
        await Assert.That(FileOperations.GetDropEffect(Drag(source, DragDropEffects.Move), fixture.Root)).IsEqualTo(DragDropEffects.None);
        await Assert.That(FileOperations.GetDropEffect(Drag(source), null)).IsEqualTo(DragDropEffects.None);
    }

    // A source that says it can be read once the drop has returned, as Explorer's own data
    // objects and so a phone's do, is: the drop starts its operation and returns, the files are
    // read on a thread of their own, and the source is told it was copied. Read before the drop
    // returns, one that can't say so would keep the window waiting on it.
    [Test]
    public async Task ASourceThatCanWaitIsReadOffTheUIThread()
    {
        using var fixture = Fixture.Create();
        var source = new VirtualSource(new VirtualSource.Entry("notes.txt", "hello"u8.ToArray(), Written: written))
        {
            Asynchronous = true
        };
        var drag = Drag(source);

        FileOperations.Drop(IntPtr.Zero, drag, fixture.At("Archive"));
        var duringDrop = source.Operations.ToList();
        await Pump.Until(() => File.Exists(fixture.At("Archive", "notes.txt")));
        var ended = await source.Ended.Task;

        await Assert.That(drag.Effect).IsEqualTo(DragDropEffects.Copy);
        await Assert.That(duringDrop).IsEquivalentTo(["start"]);
        await Assert.That(source.StartedOn).IsEqualTo(Environment.CurrentManagedThreadId);
        await Assert.That(source.ContentThreads.Distinct()).DoesNotContain(Environment.CurrentManagedThreadId);
        await Assert.That(ended).IsEqualTo((0, 1u));
        await Assert.That(await File.ReadAllTextAsync(fixture.At("Archive", "notes.txt"))).IsEqualTo("hello");
        await Assert.That(File.GetLastWriteTimeUtc(fixture.At("Archive", "notes.txt"))).IsEqualTo(written);
    }

    // One that can't is read before the drop returns, on the thread it came on.
    [Test]
    public async Task ASourceThatCantWaitIsReadBeforeTheDropReturns()
    {
        using var fixture = Fixture.Create();
        var source = new VirtualSource(new VirtualSource.Entry("notes.txt", "hello"u8.ToArray()));
        var drag = Drag(source);

        FileOperations.Drop(IntPtr.Zero, drag, fixture.At("Archive"));
        var readDuringDrop = source.ContentThreads.ToList();
        await Pump.Until(() => File.Exists(fixture.At("Archive", "notes.txt")));

        await Assert.That(drag.Effect).IsEqualTo(DragDropEffects.Copy);
        await Assert.That(readDuringDrop).IsEquivalentTo([Environment.CurrentManagedThreadId]);
        await Assert.That(source.Operations).IsEmpty();
    }

    // As a drop from another process comes, through the window's own drop target, from what
    // Explorer gives for files in a zip it has open: no paths, a folder ahead of what is in it,
    // and a stream for each file. Windows Forms keeps such a data object behind its own, where
    // Lode finds it, to ask whether it can be read once the drop has returned. The zip's says it
    // can't, whatever it is told, so it is read before the drop returns.
    [Test]
    public async Task FilesFromAZipExplorerHasOpenAreDroppedThroughTheWindow()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("photos.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using (var writer = new StreamWriter(archive.CreateEntry("top.txt").Open()))
            {
                await writer.WriteAsync("top");
            }

            await using (var writer = new StreamWriter(archive.CreateEntry("Trip/beach.txt").Open()))
            {
                await writer.WriteAsync("sand");
            }
        }

        var destination = fixture.At("Archive");
        var dropped = new List<(DragDropEffects Effect, bool SourceFound)>();
        using var form = new Form();
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AllowDrop = true
        };
        panel.DragEnter += (_, args) => args.Effect = FileOperations.GetDropEffect(args, destination);
        panel.DragOver += (_, args) => args.Effect = FileOperations.GetDropEffect(args, destination);
        panel.DragDrop += (_, args) =>
        {
            var sourceFound = VirtualFiles.Source(args.Data!) is { } source &&
                              Marshal.IsComObject(source);
            FileOperations.Drop(IntPtr.Zero, args, destination);
            dropped.Add((args.Effect, sourceFound));
        };
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();

        var data = ZipItems(zip);
        try
        {
            var target = GetProp(panel.Handle, "OleDropTargetInterface");
            await Assert.That(target).IsNotEqualTo(IntPtr.Zero);
            var methods = Marshal.ReadIntPtr(target);
            var dragEnter = Marshal.GetDelegateForFunctionPointer<DragMethod>(Marshal.ReadIntPtr(methods, 3 * IntPtr.Size));
            var drop = Marshal.GetDelegateForFunctionPointer<DragMethod>(Marshal.ReadIntPtr(methods, 6 * IntPtr.Size));
            uint effect = 1;
            dragEnter(target, data, 0, 0, ref effect);
            var entered = effect;
            effect = 1;
            drop(target, data, 0, 0, ref effect);
            await Pump.Until(() => File.Exists(fixture.At("Archive", "Trip", "beach.txt")) && File.Exists(fixture.At("Archive", "top.txt")));

            await Assert.That(entered).IsEqualTo(1u);
            await Assert.That(dropped).IsEquivalentTo([(DragDropEffects.Copy, true)]);
            await Assert.That(await File.ReadAllTextAsync(fixture.At("Archive", "top.txt"))).IsEqualTo("top");
            await Assert.That(await File.ReadAllTextAsync(fixture.At("Archive", "Trip", "beach.txt"))).IsEqualTo("sand");
        }
        finally
        {
            Marshal.Release(data);
        }
    }

    // IDropTarget's DragEnter and Drop, which take the same: the data object, the keys held,
    // where, by value, and the effect.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int DragMethod(IntPtr self, IntPtr data, uint keys, long point, ref uint effect);

    // The data object Explorer's zip folder gives for all it has at its top.
    static IntPtr ZipItems(string zip)
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName(zip, IntPtr.Zero, out var zipIdentifier, 0, out _));
        try
        {
            var folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToObject(IntPtr.Zero, zipIdentifier, IntPtr.Zero, ref folderId, out var folderPointer));
            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            var children = new List<IntPtr>();
            try
            {
                // Folders and files, hidden ones too.
                Marshal.ThrowExceptionForHR(folder.EnumObjects(IntPtr.Zero, 0x20 | 0x40 | 0x80, out var enumeratorPointer));
                var enumerator = (IEnumIdList)Marshal.GetObjectForIUnknown(enumeratorPointer);
                Marshal.Release(enumeratorPointer);
                while (enumerator.Next(1, out var child, out var fetched) == 0 &&
                       fetched == 1)
                {
                    children.Add(child);
                }

                Marshal.ReleaseComObject(enumerator);
                var dataObjectId = typeof(ComDataObject).GUID;
                Marshal.ThrowExceptionForHR(folder.GetUIObjectOf(IntPtr.Zero, (uint)children.Count, children.ToArray(), ref dataObjectId, IntPtr.Zero, out var data));
                return data;
            }
            finally
            {
                foreach (var child in children)
                {
                    Marshal.FreeCoTaskMem(child);
                }

                Marshal.ReleaseComObject(folder);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(zipIdentifier);
        }
    }

    // The bytes of the stream named Body in a compound file, as text.
    static string Body(string path)
    {
        const uint shareExclusive = 0x10;
        Marshal.ThrowExceptionForHR(StgOpenStorage(path, IntPtr.Zero, shareExclusive, IntPtr.Zero, 0, out var storage));
        try
        {
            storage.OpenStream("Body", IntPtr.Zero, shareExclusive, 0, out var stream);
            try
            {
                var buffer = new byte[64];
                var read = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    stream.Read(buffer, buffer.Length, read);
                    return Encoding.UTF8.GetString(buffer, 0, Marshal.ReadInt32(read));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(read);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(stream);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(storage);
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int StgOpenStorage(string name, IntPtr priority, uint mode, IntPtr exclude, uint reserved, out VirtualFiles.IStorage storage);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPropW")]
    static extern IntPtr GetProp(IntPtr window, string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr identifier, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    static extern int SHBindToObject(IntPtr parent, IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
}
