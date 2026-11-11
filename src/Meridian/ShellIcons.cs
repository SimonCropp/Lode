using System.Collections.Concurrent;
using static NativeMethods;

/// <summary>
/// Icons, type names and display names from the Windows shell. Icons are indexes into the
/// system image list.
/// </summary>
static class ShellIcons
{
    const uint displayNameFlag = 0x200;
    const uint typeNameFlag = 0x400;
    const uint systemIconIndexFlag = 0x4000;
    const uint smallIconFlag = 0x1;
    const uint itemIdListFlag = 0x8;
    const uint useFileAttributesFlag = 0x10;
    const uint directoryAttribute = 0x10;
    const uint normalAttribute = 0x80;
    const int computerFolder = 0x11;
    const uint imageListDrawTransparent = 0x1;

    static readonly uint infoSize = (uint)Marshal.SizeOf<ShellFileInfo>();
    // Concurrent because tests browse folders on several UI threads at once; the app has one.
    static readonly ConcurrentDictionary<string, int> iconsByExtension = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> typesByExtension = new(StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> perFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".lnk",
        ".ico",
        ".cur",
        ".ani",
        ".url",
        ".scr",
        ".appref-ms"
    };

    public static IntPtr SmallImageList { get; }
    public static int FolderIcon { get; }
    public static string FolderTypeName { get; }
    public static int ComputerIcon { get; }

    static ShellIcons()
    {
        var info = new ShellFileInfo();
        SmallImageList = SHGetFileInfo(
            "folder",
            directoryAttribute,
            ref info,
            infoSize,
            systemIconIndexFlag | smallIconFlag | useFileAttributesFlag | typeNameFlag);
        FolderIcon = info.IconIndex;
        FolderTypeName = info.TypeName;
        ComputerIcon = GetComputerIcon();
    }

    static int GetComputerIcon()
    {
        if (SHGetSpecialFolderLocation(IntPtr.Zero, computerFolder, out var itemIdList) != 0)
        {
            return FolderIcon;
        }

        try
        {
            var info = new ShellFileInfo();
            SHGetFileInfo(itemIdList, 0, ref info, infoSize, itemIdListFlag | systemIconIndexFlag | smallIconFlag);
            return info.IconIndex;
        }
        finally
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    /// <summary>
    /// The pixel size of the icons in the system image list, which follows the display scale.
    /// </summary>
    public static Size SmallIconSize
    {
        get
        {
            ImageList_GetIconSize(SmallImageList, out var width, out var height);
            return new(width, height);
        }
    }

    /// <summary>
    /// Draws a system icon onto a transparent canvas, offset from the top left. The list uses
    /// this to give its rows padding: a list row is as tall as its icon and starts at its
    /// icon's left edge, so the padding has to be part of the image.
    /// </summary>
    public static Bitmap Render(int icon, Size canvas, Point offset)
    {
        var bitmap = new Bitmap(canvas.Width, canvas.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var handle = ImageList_GetIcon(SmallImageList, icon, imageListDrawTransparent);
        if (handle == IntPtr.Zero)
        {
            return bitmap;
        }

        try
        {
            using var source = Icon.FromHandle(handle);
            using var image = source.ToBitmap();
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImage(image, new Rectangle(offset, image.Size));
        }
        finally
        {
            DestroyIcon(handle);
        }

        return bitmap;
    }

    public static int GetIcon(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderIcon;
        }

        var extension = Path.GetExtension(path);
        if (perFileIconExtensions.Contains(extension))
        {
            return GetActualIcon(path);
        }

        if (iconsByExtension.TryGetValue(extension, out var cached))
        {
            return cached;
        }

        var info = new ShellFileInfo();
        SHGetFileInfo(
            "file" + extension,
            normalAttribute,
            ref info,
            infoSize,
            systemIconIndexFlag | smallIconFlag | useFileAttributesFlag);
        iconsByExtension[extension] = info.IconIndex;
        return info.IconIndex;
    }

    /// <summary>
    /// Asks the shell for the icon of this specific item. Slower than <see cref="GetIcon"/>
    /// because it touches the disk, so it is reserved for items whose icon is not implied by
    /// their extension (drives, special folders, executables, shortcuts).
    /// </summary>
    public static int GetActualIcon(string path)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, infoSize, systemIconIndexFlag | smallIconFlag);
        return result == IntPtr.Zero ? FolderIcon : info.IconIndex;
    }

    public static string GetTypeName(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderTypeName;
        }

        var extension = Path.GetExtension(path);
        if (typesByExtension.TryGetValue(extension, out var cached))
        {
            return cached;
        }

        var info = new ShellFileInfo();
        SHGetFileInfo("file" + extension, normalAttribute, ref info, infoSize, typeNameFlag | useFileAttributesFlag);
        var typeName = string.IsNullOrEmpty(info.TypeName) ? "File" : info.TypeName;
        typesByExtension[extension] = typeName;
        return typeName;
    }

    public static (string DisplayName, string TypeName, int Icon) Describe(string path)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(
            path,
            0,
            ref info,
            infoSize,
            displayNameFlag | typeNameFlag | systemIconIndexFlag | smallIconFlag);
        if (result == IntPtr.Zero)
        {
            return (path, "", FolderIcon);
        }

        return (string.IsNullOrEmpty(info.DisplayName) ? path : info.DisplayName, info.TypeName, info.IconIndex);
    }
}
