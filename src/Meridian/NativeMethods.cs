static partial class NativeMethods
{
    public const int WindowMessageContextMenu = 0x007B;
    public const int WindowMessageAppCommand = 0x0319;
    public const int EditSetSelection = 0x00B1;

    public const int ListViewGetEditControl = 0x1018;
    public const int ListViewGetHeader = 0x101F;
    public const int ListViewSetItemState = 0x102B;
    public const int ListViewItemSelected = 0x0002;

    public const int TreeViewSetImageList = 0x1109;

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
    public struct FileOperationInfo
    {
        public IntPtr Window;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
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

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int StrCmpLogicalW(string first, string second);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowTheme(IntPtr window, string? application, string? idList);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref HeaderItem item);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ListViewItemState item);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("comctl32.dll")]
    public static partial IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImageList_GetIconSize(IntPtr imageList, out int width, out int height);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    public static partial IntPtr SHGetFileInfo(IntPtr itemIdList, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetSpecialFolderLocation(IntPtr window, int folder, out IntPtr itemIdList);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(ref FileOperationInfo info);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(ScreenPoint point);

    // POINT. System.Drawing.Point lives in another assembly, so LibraryImport can't vouch for its layout.
    [StructLayout(LayoutKind.Sequential)]
    public record struct ScreenPoint(int X, int Y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

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
}
