/// <summary>
/// Writes and reads .lnk shortcuts through the shell's ShellLink object, which works on any
/// thread, not only the UI thread's apartment.
/// </summary>
static class ShellLink
{
    // The longest path Windows has.
    const int pathLength = 32_767;

    /// <summary>
    /// Where a shortcut leads, as it says, any environment variables in it expanded. Only the
    /// shortcut is read: where it leads isn't looked for, as resolving it would, which on the
    /// network waits as long as the network does. Empty for one that leads nowhere on disk,
    /// Control Panel say.
    /// </summary>
    public static string Target(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        var buffer = Marshal.AllocCoTaskMem(pathLength * sizeof(char));
        try
        {
            ((IPersistFile)link).Load(shortcutPath, readOnly);
            Marshal.WriteInt16(buffer, 0);
            link.GetPath(buffer, pathLength, IntPtr.Zero, 0);
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
            Marshal.FinalReleaseComObject(link);
        }
    }

    // STGM_READ.
    const uint readOnly = 0;

    public static void Save(string shortcutPath, string target)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(target);
            var folder = Path.GetDirectoryName(target);
            if (folder != null)
            {
                link.SetWorkingDirectory(folder);
            }

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkObject;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath(IntPtr file, int fileLength, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription(IntPtr name, int nameLength);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr directory, int directoryLength);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(IntPtr arguments, int argumentsLength);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(IntPtr iconPath, int iconPathLength, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile(out IntPtr fileName);
    }
}
