static partial class NativeMethods
{
    public const int WindowMessageNotify = 0x004E;
    public const int WindowMessageContextMenu = 0x007B;
    public const int WindowMessageAppCommand = 0x0319;
    public const int EditSetSelection = 0x00B1;

    public const int ListViewGetImageList = 0x1002;
    public const int ListViewSetImageList = 0x1003;
    public const int ListViewImageListNormal = 0;
    public const int ListViewImageListSmall = 1;
    public const int ListViewGetStringWidth = 0x1057;
    public const int ListViewGetEditControl = 0x1018;
    public const int ListViewGetHeader = 0x101F;
    public const int HeaderGetItemRect = 0x1207;
    public const int ListViewSetItemState = 0x102B;
    public const int ListViewItemSelected = 0x0002;

    public const int TreeViewGetImageList = 0x1108;
    public const int TreeViewSetImageList = 0x1109;
    public const int TreeViewSetInsertMark = 0x111A;
    public const int TreeViewSetItem = 0x113F;
    public const uint TreeViewItemState = 0x0008;
    public const uint TreeViewItemHandle = 0x0010;
    public const uint TreeViewItemSelected = 0x0002;

    public const int HeaderDividerDoubleClick = -325;
    public const int HeaderDividerDoubleClickAnsi = -305;
    public const int HeaderEndTrack = -327;
    public const int HeaderEndTrackAnsi = -307;
    public const uint HeaderItemWidth = 0x0001;
    public const int HeaderGetItem = 0x120B;
    public const int HeaderSetItem = 0x120C;
    public const uint HeaderItemFormat = 0x0004;
    public const int HeaderFormatSortDown = 0x0200;
    public const int HeaderFormatSortUp = 0x0400;

    // SHFILEINFOW. The names are fixed buffers of UTF-16 units, as ushort rather than char, so the
    // struct is blittable without disabling runtime marshalling, which the COM interop needs.
    [StructLayout(LayoutKind.Sequential)]
    public struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        DisplayNameBuffer displayName;
        TypeNameBuffer typeName;

        public readonly string DisplayName => Text(displayName);
        public readonly string TypeName => Text(typeName);

        static string Text(ReadOnlySpan<ushort> raw)
        {
            var buffer = MemoryMarshal.Cast<ushort, char>(raw);
            var end = buffer.IndexOf((char)0);
            if (end < 0)
            {
                return new(buffer);
            }

            return new(buffer[..end]);
        }

        [InlineArray(260)]
        struct DisplayNameBuffer
        {
            ushort first;
        }

        [InlineArray(80)]
        struct TypeNameBuffer
        {
            ushort first;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HeaderItem
    {
        public uint Mask;
        public int Width;
        public IntPtr Text;
        public IntPtr Bitmap;
        public int TextLength;
        public int Format;
        public IntPtr Parameter;
        public int Image;
        public int Order;
        public uint Type;
        public IntPtr Filter;
        public uint State;
    }

    // RECT. System.Drawing.Rectangle is width and height, not right and bottom.
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ListViewItemState
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public int State;
        public int StateMask;
        public IntPtr Text;
        public int TextLength;
        public int Image;
        public IntPtr Parameter;
        public int Indent;
        public int GroupId;
        public uint ColumnCount;
        public IntPtr Columns;
        public IntPtr ColumnFormats;
        public int Group;
    }

    // TVITEMW.
    [StructLayout(LayoutKind.Sequential)]
    public struct TreeViewItem
    {
        public uint Mask;
        public IntPtr Item;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextLength;
        public int Image;
        public int SelectedImage;
        public int Children;
        public IntPtr Parameter;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int StrCmpLogicalW(string first, string second);

    // ASSOCF_IS_PROTOCOL: the association is a link's scheme ("claude"), not a file extension.
    public const int AssocIsProtocol = 0x1000;
    public const int AssocFriendlyAppName = 4;
    public const int AssocCommand = 1;

    // With no output buffer, only says how long the answer would be: S_FALSE when there is one.
    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AssocQueryString(int flags, int what, string association, string? extra, IntPtr output, ref int length);

    // Reads a string given as "@file.dll,-id", as an app's registration may name itself.
    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHLoadIndirectString(string source, [Out] char[] output, uint size, IntPtr reserved);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowTheme(IntPtr window, string? application, string? idList);

    public const int DwmWindowSystemBackdropType = 38;
    public const int DwmSystemBackdropMainWindow = 2;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref HeaderItem item);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ListViewItemState item);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref TreeViewItem item);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr window, ref ScreenPoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("comctl32.dll")]
    public static partial IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImageList_GetIconSize(IntPtr imageList, out int width, out int height);

    // Makes an image list hold this many images: those added are blank until each is replaced.
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImageList_SetImageCount(IntPtr imageList, uint count);

    // The system image list of a size, SHIL_LARGE or SHIL_EXTRALARGE say, as an HIMAGELIST.
    [LibraryImport("shell32.dll")]
    public static partial int SHGetImageList(int imageList, in Guid interfaceId, out IntPtr list);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    // Undocumented, by ordinal only, as Explorer, Notepad and the Windows Terminal use them.
    [LibraryImport("uxtheme.dll", EntryPoint = "#135")]
    public static partial int SetPreferredAppMode(int mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#136")]
    public static partial void FlushMenuThemes();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr window, nuint id);

    // Whether a key, or a mouse button, is down now, wherever the pointer is: the high bit.
    // Mouse buttons by where they are, not by what they are set to do.
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr window);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    public static partial IntPtr SHGetFileInfo(IntPtr itemIdList, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_GetCachedImageIndexW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int Shell_GetCachedImageIndex(string iconPath, int iconIndex, uint flags);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetSpecialFolderLocation(IntPtr window, int folder, out IntPtr itemIdList);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);

    public const int WindowStyle = -16;
    public const int WindowStyleVisible = 0x10000000;
    public const int WindowStyleHorizontalScroll = 0x00100000;
    public const int ScrollBarHorizontal = 0;
    public const uint ScrollInfoRange = 0x0001;
    public const uint ScrollInfoPage = 0x0002;
    public const uint ScrollInfoPosition = 0x0004;

    // SCROLLINFO.
    [StructLayout(LayoutKind.Sequential)]
    public struct ScrollInfo
    {
        public uint Size;
        public uint Mask;
        public int Minimum;
        public int Maximum;
        public uint Page;
        public int Position;
        public int TrackPosition;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);

    // The 32 bit form, which every user32 has, and which holds a window's style whole.
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr window, int index, int value);

    [LibraryImport("gdi32.dll")]
    public static partial int SaveDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RestoreDC(IntPtr dc, int saved);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OffsetViewportOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(ScreenPoint point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    // On the screen.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    public static unsafe partial int GetClassNameW(IntPtr window, char* name, int length);

    // POINT. System.Drawing.Point lives in another assembly, so LibraryImport can't vouch for its layout.
    [StructLayout(LayoutKind.Sequential)]
    public record struct ScreenPoint(int X, int Y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    // Unlike WinForms' FormWindowState.Normal, puts a minimized window back maximized if it was.
    public const int ShowWindowRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    // The code page of programs that know no Unicode, as the machine has it set.
    [LibraryImport("kernel32.dll")]
    public static partial uint GetOEMCP();

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, out uint attributesOut);

    public const int ShellChangeAssociationChanged = 0x08000000;

    [LibraryImport("shell32.dll")]
    public static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public const int AttachParentProcess = -1;
    public const int StandardOutputHandle = -11;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetStdHandle(int handle);

    // A file opened as itself, not what a link of it points at, to ask only its attributes.
    public const uint FileReadAttributes = 0x0080;
    public const uint FileShareAll = 0x0007;
    public const uint OpenExisting = 3;
    public const uint FlagOpenReparsePoint = 0x0020_0000;
    public const uint FlagBackupSemantics = 0x0200_0000;
    public const int FileAttributeTagInfo = 9;
    public const uint ReparseTagSymlink = 0xA000_000C;

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAttributeTag
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTag info, uint size);

    // What CreateDirectory fails with where something is there already. .NET's
    // Directory.CreateDirectory takes that as done, so can't say whether it made the folder.
    public const int ErrorAlreadyExists = 183;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectory(string path, IntPtr security);

    // The room on the drive a folder is on: what this user may write, which a quota can make
    // less than the drive has free, then the drive's size and what it has free. Any folder will
    // do, on a share, or a drive mounted in a folder, as DriveInfo, which takes a drive's
    // letter, can't. A share's must end in a backslash.
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string folder, out ulong freeToCaller, out ulong total, out ulong free);

    // Asking a service how it is needs no administrator permission, only these rights.
    public const uint ServiceManagerConnect = 0x0001;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr OpenService(IntPtr manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(IntPtr handle);
}
