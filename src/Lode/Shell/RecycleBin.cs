/// <summary>
/// Putting deleted items back, by the paths Windows gave them in the Recycle Bin.
/// </summary>
static partial class RecycleBin
{
    /// <summary>
    /// Restores the items to where they were deleted from, all of them in one go of the Recycle
    /// Bin's Restore, as Explorer's is on all of them selected: one progress, and one prompt for
    /// all of those with something of the same name where they go back to. Returns the paths of
    /// those not restored: no longer in the Recycle Bin, emptied since say, or still there,
    /// skipped at the shell's prompt.
    /// </summary>
    public static IReadOnlyCollection<string> Restore(IntPtr owner, IReadOnlyCollection<string> binPaths) =>
        Restore(owner, binPaths, Undelete);

    /// <summary>
    /// As <see cref="Restore(IntPtr, IReadOnlyCollection{string})"/>, each item restored by
    /// <paramref name="undelete"/>, which is given all of them at once. Tests count it.
    /// </summary>
    internal static IReadOnlyCollection<string> Restore(IntPtr owner, IReadOnlyCollection<string> binPaths, Action<IntPtr, IReadOnlyList<string>> undelete)
    {
        // Each is found by the path Windows gave it there, rather than among everything the
        // Recycle Bin holds, which may be thousands.
        var there = new HashSet<string>(binPaths.Where(Path.Exists), StringComparer.OrdinalIgnoreCase);
        if (there.Count > 0)
        {
            try
            {
                undelete(owner, there.ToList());
            }
            catch (Exception)
            {
                // What is still there is reported back.
            }
        }

        // Restored once it has left the Recycle Bin, whatever the shell says: one skipped at its
        // prompt is still there.
        return binPaths
            .Where(_ => !there.Contains(_) ||
                        Path.Exists(_))
            .ToList();
    }

    /// <summary>
    /// The Recycle Bin's own Restore, its undelete, on all the items at once, as its menu gives
    /// it for them all selected. They may be in the Recycle Bins of different drives. Done by the
    /// time it returns. On the thread of an operation: the shell's prompt and progress are
    /// shown there.
    /// </summary>
    internal static void Undelete(IntPtr owner, IReadOnlyList<string> binPaths)
    {
        Marshal.ThrowExceptionForHR(SHGetKnownFolderIDList(recycleBinFolder, 0, IntPtr.Zero, out var binIdentifier));
        IShellFolder? bin = null;
        IContextMenu? menu = null;
        var popup = IntPtr.Zero;
        var children = new List<IntPtr>(binPaths.Count);
        try
        {
            var folderId = shellFolderId;
            Marshal.ThrowExceptionForHR(SHBindToObject(IntPtr.Zero, binIdentifier, IntPtr.Zero, ref folderId, out var binPointer));
            bin = (IShellFolder)Marshal.GetObjectForIUnknown(binPointer);
            Marshal.Release(binPointer);

            // The Recycle Bin knows an item by the path Windows gave it there, on any drive.
            foreach (var binPath in binPaths)
            {
                if (bin.ParseDisplayName(owner, IntPtr.Zero, binPath, IntPtr.Zero, out var child, IntPtr.Zero) == 0)
                {
                    children.Add(child);
                }
            }

            if (children.Count == 0)
            {
                return;
            }

            var menuId = contextMenuId;
            Marshal.ThrowExceptionForHR(bin.GetUIObjectOf(owner, (uint)children.Count, children.ToArray(), ref menuId, IntPtr.Zero, out var menuPointer));
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPointer);
            Marshal.Release(menuPointer);

            // A menu is filled before any of its commands is asked for, by verb or not.
            popup = CreatePopupMenu();
            Marshal.ThrowExceptionForHR(menu.QueryContextMenu(popup, 0, firstCommand, lastCommand, 0));
            Invoke(owner, menu);
        }
        finally
        {
            if (popup != IntPtr.Zero)
            {
                DestroyMenu(popup);
            }

            if (menu != null)
            {
                Marshal.ReleaseComObject(menu);
            }

            if (bin != null)
            {
                Marshal.ReleaseComObject(bin);
            }

            foreach (var child in children)
            {
                Marshal.FreeCoTaskMem(child);
            }

            Marshal.FreeCoTaskMem(binIdentifier);
        }
    }

    static void Invoke(IntPtr owner, IContextMenu menu)
    {
        var verb = Marshal.StringToHGlobalAnsi(undelete);
        var verbUnicode = Marshal.StringToHGlobalUni(undelete);
        try
        {
            var info = new InvokeCommandInfo
            {
                Size = Marshal.SizeOf<InvokeCommandInfo>(),
                // Done before it returns, so what was restored can be told from what wasn't.
                Mask = invokeUnicode | invokeNoAsync,
                Window = owner,
                Verb = verb,
                VerbUnicode = verbUnicode,
                Show = showNormal
            };
            Marshal.ThrowExceptionForHR(menu.InvokeCommand(ref info));
        }
        finally
        {
            Marshal.FreeHGlobal(verb);
            Marshal.FreeHGlobal(verbUnicode);
        }
    }

    const string undelete = "undelete";
    const uint firstCommand = 1;
    const uint lastCommand = 0x7FFF;
    const uint invokeUnicode = 0x4000;
    const uint invokeNoAsync = 0x100;
    const int showNormal = 1;

    static readonly Guid recycleBinFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
    static readonly Guid shellFolderId = new("000214E6-0000-0000-C000-000000000046");
    static readonly Guid contextMenuId = new("000214E4-0000-0000-C000-000000000046");

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderIDList(in Guid folder, uint flags, IntPtr token, out IntPtr identifier);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToObject(IntPtr parent, IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);
}
