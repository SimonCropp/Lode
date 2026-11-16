/// <summary>
/// The shell's IFileOperation: the copy engine Explorer uses, with its progress dialog,
/// conflict prompts, Recycle Bin and undo. Unlike SHFileOperation it copes with paths longer
/// than 260 characters. It reports what each item became, which Lode's undo history needs: a
/// copy's or a move's new path (a "name - Copy" included), a renamed item's new path, and where
/// in the Recycle Bin a deleted item went.
/// </summary>
static class ShellFileOperation
{
    public const uint AllowUndo = 0x40;
    public const uint RenameOnCollision = 0x8;
    public const uint RecycleOnDelete = 0x80000;
    public const uint AddUndoRecord = 0x20000000;
    public const uint NoConfirmation = 0x10;
    // Asks before deleting outright what is too big for the Recycle Bin, even with NoConfirmation.
    public const uint WantNukeWarning = 0x4000;

    public enum Kind
    {
        Copy,
        Move,
        Delete,
        Rename
    }

    /// <summary>
    /// One item for <see cref="Run(IntPtr, Kind, IReadOnlyList{Item}, uint)"/>:
    /// <paramref name="Destination"/> is the folder a copy or move goes into, and
    /// <paramref name="NewName"/> the name it gets there, or the new name for a rename.
    /// </summary>
    public sealed record Item(string Source, string? Destination = null, string? NewName = null);

    /// <summary>
    /// An item done: what it became, as a path. For a delete, its path in the Recycle Bin, or
    /// null when it was deleted outright.
    /// </summary>
    public sealed record Change(string Source, string? Result);

    /// <summary>
    /// What happened. <paramref name="Succeeded"/> is false when the operation failed, was
    /// cancelled, or the user skipped something; <paramref name="Changes"/> lists the items done
    /// either way.
    /// </summary>
    public sealed record Outcome(bool Succeeded, IReadOnlyList<Change> Changes);

    /// <summary>
    /// Runs one operation over <paramref name="sources"/>. <paramref name="destination"/> is the
    /// folder to copy or move into, or the new name for a rename.
    /// </summary>
    public static Outcome Run(IntPtr owner, Kind kind, IReadOnlyCollection<string> sources, string? destination, uint flags)
    {
        List<Item> items;
        if (kind == Kind.Rename)
        {
            items = sources.Select(_ => new Item(_, NewName: destination)).ToList();
        }
        else
        {
            items = sources.Select(_ => new Item(_, destination)).ToList();
        }

        return Run(owner, kind, items, flags);
    }

    public static Outcome Run(IntPtr owner, Kind kind, IReadOnlyList<Item> items, uint flags)
    {
        var operation = (IFileOperation)new FileOperationObject();
        var created = new List<IShellItem>();
        var folders = new Dictionary<string, IShellItem>(StringComparer.OrdinalIgnoreCase);
        var sink = new ProgressSink();
        uint cookie = 0;
        try
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(flags);
            operation.Advise(sink, out cookie);
            foreach (var item in items)
            {
                var shellItem = CreateItem(item.Source);
                created.Add(shellItem);
                switch (kind)
                {
                    case Kind.Copy:
                        operation.CopyItem(shellItem, Folder(item.Destination!), item.NewName, IntPtr.Zero);
                        break;
                    case Kind.Move:
                        operation.MoveItem(shellItem, Folder(item.Destination!), item.NewName, IntPtr.Zero);
                        break;
                    case Kind.Delete:
                        operation.DeleteItem(shellItem, IntPtr.Zero);
                        break;
                    case Kind.Rename:
                        operation.RenameItem(shellItem, item.NewName!, IntPtr.Zero);
                        break;
                }
            }

            // Fails when the user cancels, which is not an error worth an exception.
            if (operation.PerformOperations() < 0)
            {
                return new(false, sink.Changes);
            }

            operation.GetAnyOperationsAborted(out var aborted);
            return new(!aborted, sink.Changes);
        }
        finally
        {
            if (cookie != 0)
            {
                operation.Unadvise(cookie);
            }

            foreach (var item in created.Concat(folders.Values))
            {
                Marshal.FinalReleaseComObject(item);
            }

            Marshal.FinalReleaseComObject(operation);
        }

        IShellItem Folder(string path)
        {
            if (!folders.TryGetValue(path, out var folder))
            {
                folder = CreateItem(path);
                folders[path] = folder;
            }

            return folder;
        }
    }

    static IShellItem CreateItem(string path)
    {
        var result = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        Marshal.ThrowExceptionForHR(result);
        return (IShellItem)item;
    }

    const uint fileSystemPath = 0x80058000;
    const uint desktopAbsoluteParsing = 0x80028000;

    // A file system path where the item has one, otherwise the shell's own name for it.
    static string? PathOf(IShellItem? item)
    {
        if (item == null)
        {
            return null;
        }

        foreach (var form in new[] { fileSystemPath, desktopAbsoluteParsing })
        {
            if (item.GetDisplayName(form, out var name) < 0 ||
                name == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }

        return null;
    }

    // Hears each item finish, on the thread running the operation.
    [ComVisible(true)]
    sealed class ProgressSink : IFileOperationProgressSink
    {
        List<Change> changes = [];

        // Where each item done now is, or was, for one deleted outright.
        HashSet<string>.AlternateLookup<CharSpan> places =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                .GetAlternateLookup<CharSpan>();

        /// <summary>
        /// The items done, less what is inside a folder among them. Windows copies a folder by
        /// making it and then copying each thing in it, and reports every one. What was done is
        /// the folder: sent to the Recycle Bin to undo the copy, it takes what it holds with it,
        /// and those could not then be sent after it.
        /// </summary>
        public List<Change> Changes =>
            changes
                .Where(_ => !IsInside(Place(_)))
                .ToList();

        void Record(IShellItem? item, int result, IShellItem? newlyCreated)
        {
            if (result < 0 ||
                PathOf(item) is not { } source)
            {
                return;
            }

            // Nearly all of a big copy is inside a folder reported before it, and is not kept.
            // A folder reported after what is in it is seen to when the changes are asked for.
            var change = new Change(source, PathOf(newlyCreated));
            var place = Place(change);
            if (IsInside(place))
            {
                return;
            }

            places.Set.Add(place);
            changes.Add(change);
        }

        // By where it went rather than where it was: a file picked beside the folder it is in,
        // from a search say, is copied twice, once with the folder and once by itself.
        static string Place(Change change) =>
            change.Result ?? change.Source;

        bool IsInside(string place)
        {
            for (var folder = Path.GetDirectoryName(place.AsSpan()); !folder.IsEmpty; folder = Path.GetDirectoryName(folder))
            {
                if (places.Contains(folder))
                {
                    return true;
                }
            }

            return false;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem? item, string? newName) => 0;

        public int PostRenameItem(uint flags, IShellItem? item, string? newName, int result, IShellItem? newlyCreated)
        {
            Record(item, result, newlyCreated);
            return 0;
        }

        public int PreMoveItem(uint flags, IShellItem? item, IShellItem? destination, string? newName) => 0;

        public int PostMoveItem(uint flags, IShellItem? item, IShellItem? destination, string? newName, int result, IShellItem? newlyCreated)
        {
            Record(item, result, newlyCreated);
            return 0;
        }

        public int PreCopyItem(uint flags, IShellItem? item, IShellItem? destination, string? newName) => 0;

        public int PostCopyItem(uint flags, IShellItem? item, IShellItem? destination, string? newName, int result, IShellItem? newlyCreated)
        {
            Record(item, result, newlyCreated);
            return 0;
        }

        public int PreDeleteItem(uint flags, IShellItem? item) => 0;

        public int PostDeleteItem(uint flags, IShellItem? item, int result, IShellItem? newlyCreated)
        {
            Record(item, result, newlyCreated);
            return 0;
        }

        public int PreNewItem(uint flags, IShellItem? destination, string? newName) => 0;
        public int PostNewItem(uint flags, IShellItem? destination, string? newName, string? template, uint attributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    class FileOperationObject;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int GetParent(out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(uint form, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    interface IFileOperationProgressSink
    {
        [PreserveSig]
        int StartOperations();

        [PreserveSig]
        int FinishOperations(int result);

        [PreserveSig]
        int PreRenameItem(uint flags, IShellItem? item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostRenameItem(uint flags, IShellItem? item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

        [PreserveSig]
        int PreMoveItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostMoveItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

        [PreserveSig]
        int PreCopyItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostCopyItem(uint flags, IShellItem? item, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newlyCreated);

        [PreserveSig]
        int PreDeleteItem(uint flags, IShellItem? item);

        [PreserveSig]
        int PostDeleteItem(uint flags, IShellItem? item, int result, IShellItem? newlyCreated);

        [PreserveSig]
        int PreNewItem(uint flags, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostNewItem(uint flags, IShellItem? destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? newItem);

        [PreserveSig]
        int UpdateProgress(uint total, uint done);

        [PreserveSig]
        int ResetTimer();

        [PreserveSig]
        int PauseTimer();

        [PreserveSig]
        int ResumeTimer();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        [PreserveSig]
        int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
