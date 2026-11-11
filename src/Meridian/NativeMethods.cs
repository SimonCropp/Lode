static class NativeMethods
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
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

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string first, string second);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr window, string? application, string? idList);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref HeaderItem item);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ListViewItemState item);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("comctl32.dll")]
    public static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    [DllImport("comctl32.dll")]
    public static extern bool ImageList_GetIconSize(IntPtr imageList, out int width, out int height);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(IntPtr itemIdList, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("shell32.dll")]
    public static extern int SHGetSpecialFolderLocation(IntPtr window, int folder, out IntPtr itemIdList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref FileOperationInfo info);

    public const int ShellChangeAssociationChanged = 0x08000000;

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public const int AttachParentProcess = -1;
    public const int StandardOutputHandle = -11;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetStdHandle(int handle);
}
