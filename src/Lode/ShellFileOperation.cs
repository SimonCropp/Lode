/// <summary>
/// The shell's IFileOperation: the copy engine Explorer uses, with its progress dialog,
/// conflict prompts, Recycle Bin and undo. Unlike SHFileOperation it copes with paths longer
/// than 260 characters.
/// </summary>
static class ShellFileOperation
{
    public const uint AllowUndo = 0x40;
    public const uint RenameOnCollision = 0x8;
    public const uint RecycleOnDelete = 0x80000;
    public const uint AddUndoRecord = 0x20000000;

    public enum Kind
    {
        Copy,
        Move,
        Delete,
        Rename
    }

    /// <summary>
    /// Runs one operation over <paramref name="sources"/>. <paramref name="destination"/> is the
    /// folder to copy or move into, or the new name for a rename. False when it failed, was
    /// cancelled, or the user skipped something.
    /// </summary>
    public static bool Run(IntPtr owner, Kind kind, IReadOnlyCollection<string> sources, string? destination, uint flags)
    {
        var operation = (IFileOperation)new FileOperationObject();
        var items = new List<IShellItem>();
        try
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(flags);
            IShellItem? target = null;
            if (kind is Kind.Copy or Kind.Move)
            {
                target = CreateItem(destination!);
                items.Add(target);
            }

            foreach (var source in sources)
            {
                var item = CreateItem(source);
                items.Add(item);
                switch (kind)
                {
                    case Kind.Copy:
                        operation.CopyItem(item, target!, null, IntPtr.Zero);
                        break;
                    case Kind.Move:
                        operation.MoveItem(item, target!, null, IntPtr.Zero);
                        break;
                    case Kind.Delete:
                        operation.DeleteItem(item, IntPtr.Zero);
                        break;
                    case Kind.Rename:
                        operation.RenameItem(item, destination!, IntPtr.Zero);
                        break;
                }
            }

            // Fails when the user cancels, which is not an error worth an exception.
            if (operation.PerformOperations() < 0)
            {
                return false;
            }

            operation.GetAnyOperationsAborted(out var aborted);
            return !aborted;
        }
        finally
        {
            foreach (var item in items)
            {
                Marshal.FinalReleaseComObject(item);
            }

            Marshal.FinalReleaseComObject(operation);
        }
    }

    static IShellItem CreateItem(string path)
    {
        var result = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        Marshal.ThrowExceptionForHR(result);
        return (IShellItem)item;
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
    internal interface IShellItem;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
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
