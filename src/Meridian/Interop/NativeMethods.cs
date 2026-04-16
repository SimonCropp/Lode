using System.Runtime.InteropServices;

namespace Meridian.Interop;

static class NativeMethods
{
    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_SMALLICON = 0x1;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        internal nint hIcon;
        internal int iIcon;
        internal uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern nint SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(nint hIcon);

    internal static nint GetFileIcon(string path, bool small)
    {
        var flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
        var shfi = new SHFILEINFO();
        SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return shfi.hIcon;
    }

    internal static nint GetFolderIcon(bool small)
    {
        var flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
        var shfi = new SHFILEINFO();
        SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return shfi.hIcon;
    }

    internal static void ReleaseIcon(nint hIcon)
    {
        if (hIcon != 0)
        {
            DestroyIcon(hIcon);
        }
    }
}
