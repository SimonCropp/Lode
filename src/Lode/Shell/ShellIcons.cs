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
    const int networkFolder = 0x12;
    const uint imageListDrawTransparent = 0x1;

    /// <summary>
    /// The arrow the shell draws over a shortcut's icon: the index of one of the system image
    /// list's overlays, which the list draws over any of its icons. The shell reserves this one,
    /// and sets it as it makes the list. Asking for it, with SHGetIconOverlayIndex, would load
    /// every icon overlay handler installed (cloud storage, source control) into Lode.
    /// </summary>
    public const int ShortcutOverlay = 2;

    static uint infoSize = (uint)Marshal.SizeOf<ShellFileInfo>();
    // Concurrent because tests browse folders on several UI threads at once; the app has one.
    // Looked up by span so that a cache hit, which is nearly every call, never allocates the extension.
    static ConcurrentDictionary<string, int>.AlternateLookup<CharSpan> iconsByExtension =
        new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<CharSpan>();
    static ConcurrentDictionary<string, string>.AlternateLookup<CharSpan> typesByExtension =
        new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<CharSpan>();
    static ConcurrentDictionary<string, bool>.AlternateLookup<CharSpan> shortcutsByExtension =
        new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<CharSpan>();

    static HashSet<string>.AlternateLookup<CharSpan> perFileIconExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe",
            ".lnk",
            ".ico",
            ".cur",
            ".ani",
            ".url",
            ".scr",
            ".appref-ms"
        }
            .GetAlternateLookup<CharSpan>();

    public static IntPtr SmallImageList { get; }
    public static int FolderIcon { get; }
    public static string FolderTypeName { get; }
    public static int ComputerIcon { get; }

    /// <summary>
    /// The Network folder's icon, for a share. Network is a virtual folder, so asking for its
    /// icon asks nothing of the network.
    /// </summary>
    public static int NetworkIcon { get; }

    /// <summary>
    /// Windows' own icon for a drive, for one not yet described, which asks nothing of any drive.
    /// </summary>
    public static int DriveIcon { get; }

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
        ComputerIcon = GetSpecialFolderIcon(computerFolder);
        NetworkIcon = GetSpecialFolderIcon(networkFolder);
        DriveIcon = TaskbarIdentity.StockIcon(TaskbarIdentity.StockDrive) is { } drive
            ? GetResourceIcon(drive.File, drive.Index)
            : FolderIcon;
        QuickAccessIcon = GetShellFolderIcon("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
        RecentIcon = GetShellFolderIcon("shell:::{22877a6d-37a1-461a-91b0-dbda5aaebc99}");
    }

    /// <summary>
    /// Quick access's star, for the tree's Pinned section.
    /// </summary>
    public static int QuickAccessIcon { get; }

    /// <summary>
    /// Recent folders' clock, for the tree's Recent section.
    /// </summary>
    public static int RecentIcon { get; }

    // The icon of a virtual shell folder, by its parsing name. The plain folder icon if the
    // shell does not know it.
    static int GetShellFolderIcon(string parsingName)
    {
        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var itemIdList, 0, out _) != 0)
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

    static int GetSpecialFolderIcon(int folder)
    {
        if (SHGetSpecialFolderLocation(IntPtr.Zero, folder, out var itemIdList) != 0)
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
    /// <param name="overlay">An overlay to draw over the icon, as <see cref="GetOverlay"/> gives
    /// it, or 0 for none.</param>
    public static Bitmap Render(int icon, Size canvas, Point offset, int overlay = 0)
    {
        var bitmap = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
        // The overlay's index goes in the second byte of the flags (INDEXTOOVERLAYMASK).
        var flags = imageListDrawTransparent | ((uint)overlay << 8);
        var handle = ImageList_GetIcon(SmallImageList, icon, flags);
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

    // SHIL_LARGE and SHIL_EXTRALARGE, and IImageList, whose pointer is the HIMAGELIST.
    const int largeList = 0;
    const int extraLargeList = 2;
    static Guid imageListInterface = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    // The system image lists, smallest first, for drawing an icon at a size other than the
    // small one's: a monitor at another scale than the one Windows started on.
    static Lazy<IntPtr[]> imageLists = new(() =>
    {
        var lists = new List<IntPtr> { SmallImageList };
        foreach (var size in (int[])[largeList, extraLargeList])
        {
            if (SHGetImageList(size, imageListInterface, out var list) >= 0 &&
                list != IntPtr.Zero)
            {
                lists.Add(list);
            }
        }

        return [.. lists];
    });

    /// <summary>
    /// As <see cref="Render(int, Size, Point, int)"/>, with the icon drawn
    /// <paramref name="iconSize"/> pixels square: from the smallest of the system's image lists
    /// that holds it at least that big, scaled down to it.
    /// </summary>
    public static Bitmap Render(int icon, Size canvas, Point offset, int overlay, int iconSize)
    {
        var lists = imageLists.Value;
        var source = lists[^1];
        foreach (var list in lists)
        {
            ImageList_GetIconSize(list, out var width, out _);
            if (width >= iconSize)
            {
                source = list;
                break;
            }
        }

        var bitmap = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
        var flags = imageListDrawTransparent | ((uint)overlay << 8);
        var handle = ImageList_GetIcon(source, icon, flags);
        if (handle == IntPtr.Zero)
        {
            return bitmap;
        }

        try
        {
            using var drawn = Icon.FromHandle(handle);
            using var image = drawn.ToBitmap();
            using var graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(offset, new(iconSize, iconSize)));
        }
        finally
        {
            DestroyIcon(handle);
        }

        return bitmap;
    }

    public static int GetIcon(string path, bool isDirectory) =>
        GetIcon(path, isDirectory, null);

    /// <param name="modified">When the file was last written, for one listed in a folder. Its
    /// own icon, if it has one, is then remembered until it is written again.</param>
    /// <param name="known">For a file that has its own icon, a program or shortcut, not known
    /// yet: the file is read for it off the thread that asked, which is told once it is known,
    /// and is given the icon its extension has until then. Reading it can take a while for a
    /// file on a share, or a shortcut to somewhere on the network. Without this, it is read
    /// there and then.</param>
    public static int GetIcon(string path, bool isDirectory, DateTime? modified, Action? known = null)
    {
        if (isDirectory)
        {
            return FolderIcon;
        }

        var extension = Path.GetExtension(path.AsSpan());
        if (perFileIconExtensions.Contains(extension))
        {
            if (modified is not { } written)
            {
                return GetActualIcon(path);
            }

            if (known == null)
            {
                return GetFileIcon(path, written);
            }

            if (iconsByFile.TryGetValue(path, out var remembered) &&
                remembered.Modified == written)
            {
                return remembered.Icon;
            }

            Tell(ReadFileIcon(path, written), known);
            return ExtensionIcon(extension);
        }

        return ExtensionIcon(extension);
    }

    /// <summary>
    /// The icon for a file of <paramref name="path"/>'s type, or a folder, without reading it:
    /// for one that isn't on disk, in a zip say, where a program's or shortcut's own can't be read.
    /// </summary>
    public static int GetTypeIcon(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderIcon;
        }

        return ExtensionIcon(Path.GetExtension(path.AsSpan()));
    }

    // Each file being read for its icon, so asking again while it is waits for the same read.
    static ConcurrentDictionary<(string Path, DateTime Modified), Task> readingFileIcons = new();

    static Task ReadFileIcon(string path, DateTime modified) =>
        readingFileIcons.GetOrAdd(
            (path, modified),
            static key => Task.Run(
                () =>
                {
                    try
                    {
                        GetFileIcon(key.Path, key.Modified);
                    }
                    finally
                    {
                        readingFileIcons.TryRemove(key, out _);
                    }
                }));

    // Back on the thread that asked, as awaiting there returns to it.
    static async void Tell(Task reading, Action known)
    {
        try
        {
            await reading;
        }
        catch (Exception)
        {
            // The extension's icon stays.
            return;
        }

        known();
    }

    // The icon every file of the extension shows, from the extension alone.
    static int ExtensionIcon(CharSpan extension)
    {
        if (iconsByExtension.TryGetValue(extension, out var cached))
        {
            return cached;
        }

        var info = new ShellFileInfo();
        SHGetFileInfo(
            string.Concat("file", extension),
            normalAttribute,
            ref info,
            infoSize,
            systemIconIndexFlag | smallIconFlag | useFileAttributesFlag);
        iconsByExtension[extension] = info.IconIndex;
        return info.IconIndex;
    }

    /// <summary>
    /// The overlay Explorer draws over an item's icon in a folder: <see cref="ShortcutOverlay"/>
    /// for a shortcut, or 0 for none. Goes by the item's type, so asks nothing of the disk.
    /// </summary>
    public static int GetOverlay(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return 0;
        }

        var extension = Path.GetExtension(path.AsSpan());
        if (!shortcutsByExtension.TryGetValue(extension, out var shortcut))
        {
            shortcut = IsShortcutType(extension.ToString());
            shortcutsByExtension[extension] = shortcut;
        }

        if (shortcut)
        {
            return ShortcutOverlay;
        }

        return 0;
    }

    // The shell counts a file as a shortcut, and draws the arrow over its icon, when its file
    // type says IsShortcut, as those of .lnk, .url, .pif, .appref-ms and .website files do.
    static bool IsShortcutType(string extension)
    {
        if (extension.Length == 0)
        {
            return false;
        }

        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
        if (extensionKey?.GetValue(null) is not string type ||
            type.Length == 0)
        {
            return false;
        }

        using var typeKey = Registry.ClassesRoot.OpenSubKey(type);
        return typeKey?.GetValue("IsShortcut") != null;
    }

    /// <summary>
    /// Asks the shell for the icon of this specific item. Slower than <see cref="GetIcon"/>
    /// because it touches the disk, so it is reserved for items whose icon is not implied by
    /// their extension (drives, special folders, executables, shortcuts).
    /// </summary>
    const uint iconHandleFlag = 0x100;

    /// <summary>
    /// The shell's plain folder icon, for the tray and for a window before it shows a folder.
    /// The caller owns it.
    /// </summary>
    public static Icon FolderWindowIcon()
    {
        var info = new ShellFileInfo();
        SHGetFileInfo("folder", directoryAttribute, ref info, infoSize, iconHandleFlag | useFileAttributesFlag);
        return Owned(info.Icon);
    }

    // A managed copy of a shell icon handle, releasing the handle.
    static Icon Owned(IntPtr handle)
    {
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static int GetActualIcon(string path)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, infoSize, systemIconIndexFlag | smallIconFlag);
        return result == IntPtr.Zero ? FolderIcon : info.IconIndex;
    }

    // The icons of files that have their own (programs, shortcuts), each as of when the file was
    // last written. Asking the shell reads the file, and a folder's rows are made again for every
    // sort, filter and change on disk.
    static ConcurrentDictionary<string, (DateTime Modified, int Icon)> iconsByFile = new(StringComparer.OrdinalIgnoreCase);

    // Browsing keeps finding more programs. Past this many, start again rather than keep them all.
    const int maximumFileIcons = 4096;

    static int GetFileIcon(string path, DateTime modified)
    {
        if (iconsByFile.TryGetValue(path, out var known) &&
            known.Modified == modified)
        {
            return known.Icon;
        }

        var icon = GetActualIcon(path);
        if (iconsByFile.Count >= maximumFileIcons)
        {
            iconsByFile.Clear();
        }

        iconsByFile[path] = (modified, icon);
        return icon;
    }

    /// <summary>
    /// Asks the shell again for the icons of files that have their own, for Refresh: a
    /// shortcut's icon follows what it points at, which can change without the shortcut.
    /// </summary>
    public static void ForgetFileIcons() =>
        iconsByFile.Clear();

    /// <summary>
    /// The icon a program or library holds among its resources, at <paramref name="index"/>
    /// (a resource id when negative), as file types and apps name theirs.
    /// </summary>
    public static int GetResourceIcon(string path, int index) =>
        Shell_GetCachedImageIndex(path, index, 0);

    public static string GetTypeName(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderTypeName;
        }

        var extension = Path.GetExtension(path.AsSpan());
        if (typesByExtension.TryGetValue(extension, out var cached))
        {
            return cached;
        }

        var info = new ShellFileInfo();
        SHGetFileInfo(string.Concat("file", extension),normalAttribute, ref info, infoSize, typeNameFlag | useFileAttributesFlag);
        var typeName = Unregistered(string.IsNullOrEmpty(info.TypeName) ? "File" : info.TypeName, extension);
        typesByExtension[extension] = typeName;
        return typeName;
    }

    /// <summary>
    /// <paramref name="typeName"/>, but for a type nothing is registered for, which Windows names
    /// after its extension in capitals, "ALEPH File", the extension in lower case, as files are
    /// mostly named: "aleph File". Windows in another language words it otherwise, and keeps it.
    /// </summary>
    internal static string Unregistered(string typeName, CharSpan extension)
    {
        if (extension.Length < 2)
        {
            return typeName;
        }

        var name = extension[1..].ToString();
        if (!string.Equals(typeName, $"{name.ToUpperInvariant()} File", StringComparison.Ordinal))
        {
            return typeName;
        }

        return $"{name.ToLowerInvariant()} File";
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

    /// <summary>
    /// A folder's own icon, as a tab or the tree shows it: a drive's, Downloads' arrow. A folder
    /// on the network gets the plain folder icon, since asking for its own could keep the window
    /// waiting on the network, and a drive mapped to a share its icon once it has been described.
    /// </summary>
    public static int GetPlaceIcon(string path)
    {
        if (!Locations.IsRemote(path))
        {
            return placeIcons.GetOrAdd(
                path,
                static _ =>
                {
                    Interlocked.Increment(ref placeIconsRead);
                    return GetActualIcon(_);
                });
        }

        if (!Locations.IsNetwork(path) &&
            Path.GetPathRoot(path) is { } root &&
            Locations.Same(root, path))
        {
            return DescribeDrive(path, null).Icon;
        }

        return FolderIcon;
    }

    // The icons of places, as tabs and Pinned show them, each asked of the shell once: every tab
    // is named again on every navigation. Forgotten on Refresh, as a folder's icon can change,
    // with its desktop.ini say.
    static ConcurrentDictionary<string, int> placeIcons = new(StringComparer.OrdinalIgnoreCase);
    static int placeIconsRead;

    /// <summary>
    /// How many times the shell has been asked for a place's icon. For tests.
    /// </summary>
    internal static int PlaceIconsRead => Volatile.Read(ref placeIconsRead);

    /// <summary>
    /// Asks the shell again for places' icons, for Refresh.
    /// </summary>
    public static void ForgetPlaceIcons() =>
        placeIcons.Clear();

    // Drives as the shell describes them, or is still describing them. The shell reads the drive
    // for its name and icon: one mapped to a share asks the share, which waits on the network,
    // long when the share is offline, and one just plugged in, a disk spinning up or a card
    // reader, can be slow to answer too. So each is described off the UI thread.
    static ConcurrentDictionary<string, Task<(string DisplayName, string TypeName, int Icon)>> drives =
        new(StringComparer.OrdinalIgnoreCase);

    // How each was last described, to show while it is being described again.
    static ConcurrentDictionary<string, (string DisplayName, string TypeName, int Icon)> describedDrives =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Describes a drive, as <see cref="Describe"/> does, off the UI thread. Tests swap it, to
    /// describe drives at once, or only once they say.
    /// </summary>
    internal static Func<string, Task<(string DisplayName, string TypeName, int Icon)>> DescribeOffUiThread { get; set; } =
        static drive => Task.Run(() => Describe(drive));

    /// <summary>
    /// As <see cref="Describe"/>, for a drive, which is described off the UI thread: until it
    /// has been, it is as it was last described, or named by its letter with a drive's icon, or
    /// Network's for one mapped to a share, and <paramref name="described"/> is called, on the
    /// calling thread, once it has.
    /// </summary>
    public static (string DisplayName, string TypeName, int Icon) DescribeDrive(string drive, Action? described)
    {
        var describing = drives.GetOrAdd(drive, static _ => Remember(_, DescribeOffUiThread(_)));
        if (describing.IsCompletedSuccessfully)
        {
            return describing.Result;
        }

        if (described != null)
        {
            Tell(describing, described);
        }

        if (describedDrives.TryGetValue(drive, out var last))
        {
            return last;
        }

        var letter = drive.TrimEnd(Path.DirectorySeparatorChar);
        if (Locations.IsRemote(drive))
        {
            return (letter, "", NetworkIcon);
        }

        return (letter, "", DriveIcon);

        static async Task<(string DisplayName, string TypeName, int Icon)> Remember(string drive, Task<(string DisplayName, string TypeName, int Icon)> describing)
        {
            var description = await describing.ConfigureAwait(false);
            describedDrives[drive] = description;
            return description;
        }

        // Back on the thread that asked, as awaiting there returns to it.
        static async void Tell(Task describing, Action described)
        {
            try
            {
                await describing;
            }
            catch (Exception)
            {
                // Still named by its letter.
                return;
            }

            described();
        }
    }

    /// <summary>
    /// Describes drives again when next asked, for Refresh and when drives come and go: another
    /// disk can be plugged in under a letter, a drive renamed, or mapped to another share. Each
    /// is shown as it was last described until it has been.
    /// </summary>
    public static void ForgetDrives() =>
        drives.Clear();
}
