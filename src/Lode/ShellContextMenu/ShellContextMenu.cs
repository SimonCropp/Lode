/// <summary>
/// Shows the classic Windows shell context menu (the IContextMenu one, with every installed
/// shell extension) for files, folders, or a folder's background.
/// </summary>
sealed partial class ShellContextMenu : NativeWindow, IDisposable
{
    public record CustomCommand(string Text, Action Action);

    const uint firstShellCommand = 0x100;
    const uint lastShellCommand = 0x7FFF;

    const uint canRenameFlag = 0x10;
    const uint extendedVerbsFlag = 0x100;

    const uint menuString = 0x0;
    const uint menuSeparator = 0x800;
    const uint menuByPosition = 0x400;
    const uint trackReturnCommand = 0x100;
    const uint trackRightButton = 0x2;

    const int initMenuPopupMessage = 0x117;
    const int menuCharMessage = 0x120;
    const int drawItemMessage = 0x2B;
    const int measureItemMessage = 0x2C;

    const uint commandVerbUnicode = 0x4;
    const uint invokeUnicode = 0x4000;
    const uint invokePointValid = 0x20000000;
    const uint invokeShiftDown = 0x10000000;
    const uint invokeControlDown = 0x40000000;
    const int showNormal = 1;

    static readonly Guid shellFolderId = new("000214E6-0000-0000-C000-000000000046");
    static readonly Guid contextMenuId = new("000214E4-0000-0000-C000-000000000046");

    IContextMenu2? menu2;
    IContextMenu3? menu3;

    public ShellContextMenu() =>
        CreateHandle(new());

    public void Dispose() =>
        DestroyHandle();

    /// <summary>
    /// Shows the menu for one or more items that share a parent folder.
    /// <paramref name="handleVerb"/> gets first refusal on the chosen command: return true to
    /// stop the shell from running it.
    /// </summary>
    public bool ShowForItems(
        IntPtr owner,
        IReadOnlyList<string> paths,
        Point location,
        IReadOnlyList<CustomCommand> customCommands,
        Func<string, bool> handleVerb)
    {
        var contextMenu = CreateForItems(owner, paths);
        if (contextMenu == null)
        {
            return false;
        }

        Show(contextMenu, owner, location, customCommands, customFirst: false, canRenameFlag, handleVerb);
        return true;
    }

    /// <summary>
    /// Shows the menu for a folder as Quick access lists it, so the shell offers Unpin for a
    /// pinned folder rather than Pin, as Explorer does in Home. Falls back to the folder's own
    /// menu when Quick access no longer lists it.
    /// </summary>
    public bool ShowForQuickAccessItem(
        IntPtr owner,
        string path,
        Point location,
        IReadOnlyList<CustomCommand> customCommands,
        Func<string, bool> handleVerb)
    {
        var contextMenu = CreateForQuickAccessItem(owner, path);
        if (contextMenu == null)
        {
            return ShowForItems(owner, [path], location, customCommands, handleVerb);
        }

        Show(contextMenu, owner, location, customCommands, customFirst: false, canRenameFlag, handleVerb);
        return true;
    }

    // The shell folder behind Explorer's Quick access list.
    const string quickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
    const uint fileSystemPathName = 0x80058000;

    static IContextMenu? CreateForQuickAccessItem(IntPtr owner, string path)
    {
        if (SHParseDisplayName(quickAccess, IntPtr.Zero, out var parentId, 0, out _) != 0)
        {
            return null;
        }

        IShellFolder? folder = null;
        IEnumIdList? enumerator = null;
        try
        {
            var folderId = shellFolderId;
            if (SHBindToObject(IntPtr.Zero, parentId, IntPtr.Zero, ref folderId, out var folderPointer) != 0)
            {
                return null;
            }

            folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);

            // Folders only; Quick access also lists recent files.
            const int foldersFlag = 0x20;
            if (folder.EnumObjects(owner, foldersFlag, out var enumeratorPointer) != 0 ||
                enumeratorPointer == IntPtr.Zero)
            {
                return null;
            }

            enumerator = (IEnumIdList)Marshal.GetObjectForIUnknown(enumeratorPointer);
            Marshal.Release(enumeratorPointer);
            while (enumerator.Next(1, out var child, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (!Locations.Same(ChildPath(parentId, child), path))
                    {
                        continue;
                    }

                    var menuId = contextMenuId;
                    var result = folder.GetUIObjectOf(owner, 1, [child], ref menuId, IntPtr.Zero, out var menuPointer);
                    if (result != 0)
                    {
                        return null;
                    }

                    return Wrap(menuPointer);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(child);
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (enumerator != null)
            {
                Marshal.ReleaseComObject(enumerator);
            }

            if (folder != null)
            {
                Marshal.ReleaseComObject(folder);
            }

            Marshal.FreeCoTaskMem(parentId);
        }
    }

    static string? ChildPath(IntPtr parentId, IntPtr child)
    {
        var full = ILCombine(parentId, child);
        try
        {
            if (SHGetNameFromIDList(full, fileSystemPathName, out var name) != 0)
            {
                return null;
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
        finally
        {
            Marshal.FreeCoTaskMem(full);
        }
    }

    /// <summary>
    /// Shows the menu for the empty area of a folder (New, Paste, Properties and extensions).
    /// </summary>
    public bool ShowForFolder(
        IntPtr owner,
        string folder,
        Point location,
        IReadOnlyList<CustomCommand> customCommands,
        Func<string, bool> handleVerb)
    {
        var contextMenu = CreateForFolder(owner, folder);
        if (contextMenu == null)
        {
            return false;
        }

        Show(contextMenu, owner, location, customCommands, customFirst: true, 0, handleVerb);
        return true;
    }

    /// <summary>
    /// Runs a shell verb such as "properties" against the items without showing a menu.
    /// </summary>
    public static void InvokeVerb(IntPtr owner, IReadOnlyList<string> paths, string verb)
    {
        var contextMenu = CreateForItems(owner, paths);
        if (contextMenu == null)
        {
            return;
        }

        InvokeVerb(contextMenu, owner, verb);
    }

    /// <summary>
    /// Pins a folder to Quick access, or unpins it, as the shell's own menu entries do.
    /// Unpin is only offered by the folder's Quick access entry, so it is asked for that.
    /// </summary>
    public static void SetPinned(IntPtr owner, string path, bool pin)
    {
        IContextMenu? contextMenu;
        if (pin)
        {
            contextMenu = CreateForItems(owner, [path]);
        }
        else
        {
            contextMenu = CreateForQuickAccessItem(owner, path);
        }

        if (contextMenu == null)
        {
            return;
        }

        InvokeVerb(contextMenu, owner, pin ? "pintohome" : "unpinfromhome");
        QuickAccessChanged?.Invoke();
    }

    static void InvokeVerb(IContextMenu contextMenu, IntPtr owner, string verb)
    {
        var menu = CreatePopupMenu();
        var verbAnsi = Marshal.StringToHGlobalAnsi(verb);
        var verbUnicode = Marshal.StringToHGlobalUni(verb);
        try
        {
            contextMenu.QueryContextMenu(menu, 0, firstShellCommand, lastShellCommand, 0);
            var info = new InvokeCommandInfo
            {
                Size = Marshal.SizeOf<InvokeCommandInfo>(),
                Mask = invokeUnicode,
                Window = owner,
                Verb = verbAnsi,
                VerbUnicode = verbUnicode,
                Show = showNormal
            };
            contextMenu.InvokeCommand(ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(verbAnsi);
            Marshal.FreeHGlobal(verbUnicode);
            DestroyMenu(menu);
            Marshal.ReleaseComObject(contextMenu);
        }
    }

    void Show(
        IContextMenu contextMenu,
        IntPtr owner,
        Point location,
        IReadOnlyList<CustomCommand> customCommands,
        bool customFirst,
        uint flags,
        Func<string, bool> handleVerb)
    {
        Action? customAction = null;
        var quickAccessChanged = false;
        var menu = CreatePopupMenu();
        try
        {
            if (Control.ModifierKeys.HasFlag(Keys.Shift))
            {
                flags |= extendedVerbsFlag;
            }

            if (contextMenu.QueryContextMenu(menu, 0, firstShellCommand, lastShellCommand, flags) < 0)
            {
                return;
            }

            RemoveUnwanted(contextMenu, menu);
            InsertCustomCommands(menu, customCommands, customFirst);

            menu2 = contextMenu as IContextMenu2;
            menu3 = contextMenu as IContextMenu3;

            var command = TrackPopupMenuEx(
                menu,
                trackReturnCommand | trackRightButton,
                location.X,
                location.Y,
                Handle,
                IntPtr.Zero);

            if (command >= firstShellCommand)
            {
                var offset = command - firstShellCommand;
                var verb = GetVerb(contextMenu, offset);
                if (verb == null || !handleVerb(verb))
                {
                    Invoke(contextMenu, owner, offset, location);
                    if (verb != null && quickAccessVerbs.Contains(verb))
                    {
                        quickAccessChanged = true;
                    }
                }
            }
            else if (command > 0)
            {
                customAction = customCommands[(int)command - 1].Action;
            }
        }
        finally
        {
            menu2 = null;
            menu3 = null;
            DestroyMenu(menu);
            Marshal.ReleaseComObject(contextMenu);
        }

        if (quickAccessChanged)
        {
            QuickAccessChanged?.Invoke();
        }

        customAction?.Invoke();
    }

    /// <summary>
    /// Raised after a menu pinned a folder to Quick access or unpinned one.
    /// </summary>
    public static event Action? QuickAccessChanged;

    static readonly HashSet<string> quickAccessVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pintohome",
        "unpinfromhome"
    };

    // Entries left out of every menu, by verb, which unlike the text is the same in every
    // language.
    static readonly HashSet<string> removedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Restore previous versions
        "PreviousVersions",
        // Edit in Notepad, added by the Notepad app
        "{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"
    };

    /// <summary>
    /// Takes out the entries Lode leaves off: those in <see cref="removedVerbs"/>, plus
    /// Send to and Include in library. Those two are submenus with no verb, so they are known
    /// by what they list: the SendTo folder, and the user's libraries.
    /// </summary>
    static void RemoveUnwanted(IContextMenu contextMenu, IntPtr menu)
    {
        var sendTo = DisplayNames(Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        var libraries = DisplayNames(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Libraries"));
        for (var position = GetMenuItemCount(menu) - 1; position >= 0; position--)
        {
            var submenu = GetSubMenu(menu, position);
            bool remove;
            if (submenu != IntPtr.Zero)
            {
                remove = Lists(submenu, sendTo) || Lists(submenu, libraries);
                if (!remove && (sendTo.Count > 0 || libraries.Count > 0) && LoneEntry(submenu))
                {
                    // Include in library holds only a "Retrieving libraries..." placeholder until
                    // the shell fills it in, which it does on a message posted back to this
                    // thread. Send to can be filled in late the same way, which CI hits where a
                    // local run does not. Pump briefly so the shell can, then look again for both.
                    var stopwatch = Stopwatch.StartNew();
                    while (LoneEntry(submenu) && stopwatch.ElapsedMilliseconds < 500)
                    {
                        Application.DoEvents();
                        Thread.Sleep(5);
                    }

                    remove = Lists(submenu, sendTo) || Lists(submenu, libraries);
                }
            }
            else
            {
                var id = GetMenuItemID(menu, position);
                remove = id is >= firstShellCommand and <= lastShellCommand &&
                         GetVerb(contextMenu, id - firstShellCommand) is { } verb &&
                         removedVerbs.Contains(verb);
            }

            if (remove)
            {
                DeleteMenu(menu, (uint)position, menuByPosition);
            }
        }

        TidySeparators(menu);
    }

    static bool LoneEntry(IntPtr submenu) =>
        GetMenuItemCount(submenu) == 1;

    // Asking the shell for every name took most of the time this class adds to a right-click, so
    // each folder's names are kept, and read again only once the folder has changed.
    static readonly ConcurrentDictionary<string, (DateTime Changed, HashSet<string> Names)> namesByFolder =
        new(StringComparer.OrdinalIgnoreCase);

    // What Explorer calls each item in a folder, such as "Mail recipient" for
    // Mail Recipient.MAPIMail.
    static HashSet<string> DisplayNames(string folder)
    {
        var changed = Directory.GetLastWriteTimeUtc(folder);
        if (namesByFolder.TryGetValue(folder, out var cached) && cached.Changed == changed)
        {
            return cached.Names;
        }

        var names = ReadDisplayNames(folder);
        namesByFolder[folder] = (changed, names);
        return names;
    }

    static HashSet<string> ReadDisplayNames(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (!File.GetAttributes(path).HasFlag(FileAttributes.Hidden))
                {
                    names.Add(ShellIcons.Describe(path).DisplayName);
                }
            }
        }
        catch (Exception)
        {
            // No such folder: nothing to recognise a submenu by.
        }

        return names;
    }

    // True when a submenu lists those names. One entry may be something else, such as Include
    // in library's "Create new library".
    static bool Lists(IntPtr submenu, HashSet<string> names)
    {
        if (names.Count == 0)
        {
            return false;
        }

        var texts = Enumerable.Range(0, GetMenuItemCount(submenu))
            .Select(_ => MenuText(submenu, _).Replace("&", ""))
            .Where(_ => _.Length > 0)
            .ToList();
        return texts.Count > 0 && texts.Count(names.Contains) >= Math.Max(1, texts.Count - 1);
    }

    static unsafe string MenuText(IntPtr menu, int position)
    {
        const int capacity = 256;
        var text = stackalloc char[capacity];
        var length = GetMenuString(menu, (uint)position, text, capacity, menuByPosition);
        return new(text, 0, Math.Max(0, length));
    }

    static bool IsSeparator(IntPtr menu, int position) =>
        (GetMenuState(menu, (uint)position, menuByPosition) & menuSeparator) != 0;

    // Removes separators left at either end, or next to each other, by removed entries.
    static void TidySeparators(IntPtr menu)
    {
        for (var position = GetMenuItemCount(menu) - 1; position >= 0; position--)
        {
            var last = position == GetMenuItemCount(menu) - 1;
            if (IsSeparator(menu, position) &&
                (position == 0 || last || IsSeparator(menu, position + 1)))
            {
                DeleteMenu(menu, (uint)position, menuByPosition);
            }
        }
    }

    /// <summary>
    /// Builds and discards the menu for a folder and for a file, so Windows loads the installed
    /// shell extensions before the first real right-click. Otherwise that click waits a few
    /// hundred milliseconds for them, where later ones take a few dozen.
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            Describe(IntPtr.Zero, [Locations.Home]);
            Describe(IntPtr.Zero, [Environment.ProcessPath!]);
        }
        catch (Exception)
        {
            // Only a head start; the first right-click does the same work if this could not.
        }
    }

    public record MenuEntry(string Text, string? Verb, IReadOnlyList<MenuEntry> Submenu);

    /// <summary>
    /// The top level of the menu the shell builds for the items, as Lode would show it.
    /// </summary>
    public static List<MenuEntry> Describe(IntPtr owner, IReadOnlyList<string> paths)
    {
        var contextMenu = CreateForItems(owner, paths);
        if (contextMenu == null)
        {
            return [];
        }

        var menu = CreatePopupMenu();
        try
        {
            contextMenu.QueryContextMenu(menu, 0, firstShellCommand, lastShellCommand, canRenameFlag);
            RemoveUnwanted(contextMenu, menu);
            return DescribeMenu(contextMenu, menu);
        }
        finally
        {
            DestroyMenu(menu);
            Marshal.ReleaseComObject(contextMenu);
        }
    }

    static List<MenuEntry> DescribeMenu(IContextMenu contextMenu, IntPtr menu)
    {
        var entries = new List<MenuEntry>();
        for (var position = 0; position < GetMenuItemCount(menu); position++)
        {
            var id = GetMenuItemID(menu, position);
            var verb = id is >= firstShellCommand and <= lastShellCommand ? GetVerb(contextMenu, id - firstShellCommand) : null;
            var submenu = GetSubMenu(menu, position);
            entries.Add(new(MenuText(menu, position), verb, submenu == IntPtr.Zero ? [] : DescribeMenu(contextMenu, submenu)));
        }

        return entries;
    }

    static void InsertCustomCommands(IntPtr menu, IReadOnlyList<CustomCommand> customCommands, bool customFirst)
    {
        if (customCommands.Count == 0)
        {
            return;
        }

        // On an item menu the shell's default verb stays on top and ours follow it.
        var position = customFirst ? 0u : (uint)Math.Min(1, GetMenuItemCount(menu));
        for (var index = 0; index < customCommands.Count; index++)
        {
            InsertMenu(menu, position, menuByPosition | menuString, (UIntPtr)(index + 1), customCommands[index].Text);
            position++;
        }

        if (customFirst)
        {
            InsertMenu(menu, position, menuByPosition | menuSeparator, UIntPtr.Zero, null);
        }
    }

    static string? GetVerb(IContextMenu contextMenu, uint offset)
    {
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (contextMenu.GetCommandString(offset, commandVerbUnicode, IntPtr.Zero, buffer, 255) != 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception)
        {
            // Some shell extensions fault when asked for a verb; treat them as verbless.
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static void Invoke(IContextMenu contextMenu, IntPtr owner, uint offset, Point location)
    {
        var mask = invokeUnicode | invokePointValid;
        if (Control.ModifierKeys.HasFlag(Keys.Shift))
        {
            mask |= invokeShiftDown;
        }

        if (Control.ModifierKeys.HasFlag(Keys.Control))
        {
            mask |= invokeControlDown;
        }

        var info = new InvokeCommandInfo
        {
            Size = Marshal.SizeOf<InvokeCommandInfo>(),
            Mask = mask,
            Window = owner,
            Verb = (IntPtr)offset,
            VerbUnicode = (IntPtr)offset,
            Show = showNormal,
            InvokePoint = location
        };
        contextMenu.InvokeCommand(ref info);
    }

    static IContextMenu? CreateForItems(IntPtr owner, IReadOnlyList<string> paths)
    {
        var identifiers = new List<IntPtr>();
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var identifier, 0, out _) == 0)
                {
                    identifiers.Add(identifier);
                }
            }

            if (identifiers.Count == 0)
            {
                return null;
            }

            var folderId = shellFolderId;
            if (SHBindToParent(identifiers[0], ref folderId, out var folderPointer, out _) != 0)
            {
                return null;
            }

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            try
            {
                var children = identifiers.Select(ILFindLastID).ToArray();
                var menuId = contextMenuId;
                var result = folder.GetUIObjectOf(
                    owner,
                    (uint)children.Length,
                    children,
                    ref menuId,
                    IntPtr.Zero,
                    out var menuPointer);
                return result == 0 ? Wrap(menuPointer) : null;
            }
            finally
            {
                Marshal.ReleaseComObject(folder);
            }
        }
        finally
        {
            foreach (var identifier in identifiers)
            {
                Marshal.FreeCoTaskMem(identifier);
            }
        }
    }

    static IContextMenu? CreateForFolder(IntPtr owner, string folderPath)
    {
        if (SHParseDisplayName(folderPath, IntPtr.Zero, out var identifier, 0, out _) != 0)
        {
            return null;
        }

        try
        {
            var folderId = shellFolderId;
            if (SHBindToObject(IntPtr.Zero, identifier, IntPtr.Zero, ref folderId, out var folderPointer) != 0)
            {
                return null;
            }

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            try
            {
                var menuId = contextMenuId;
                var result = folder.CreateViewObject(owner, ref menuId, out var menuPointer);
                return result == 0 ? Wrap(menuPointer) : null;
            }
            finally
            {
                Marshal.ReleaseComObject(folder);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(identifier);
        }
    }

    static IContextMenu Wrap(IntPtr menuPointer)
    {
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPointer);
        Marshal.Release(menuPointer);
        return menu;
    }

    // Submenus such as "Send to", "Open with" and "New" are populated and drawn by the shell,
    // which needs these messages forwarded from the window that owns the popup.
    protected override void WndProc(ref Message message)
    {
        var isMenuMessage =
            message.Msg is initMenuPopupMessage or measureItemMessage or drawItemMessage or menuCharMessage;
        if (isMenuMessage)
        {
            if (menu3 != null)
            {
                if (menu3.HandleMenuMsg2((uint)message.Msg, message.WParam, message.LParam, out var result) == 0)
                {
                    message.Result = result;
                    return;
                }
            }
            else if (menu2 != null && message.Msg != menuCharMessage)
            {
                if (menu2.HandleMenuMsg((uint)message.Msg, message.WParam, message.LParam) == 0)
                {
                    message.Result = message.Msg == initMenuPopupMessage ? 0 : 1;
                    return;
                }
            }
        }

        base.WndProc(ref message);
    }
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr identifier, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(IntPtr identifier, ref Guid interfaceId, out IntPtr parent, out IntPtr child);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToObject(IntPtr parent, IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [LibraryImport("shell32.dll")]
    private static partial IntPtr ILFindLastID(IntPtr identifier);

    [LibraryImport("shell32.dll")]
    private static partial IntPtr ILCombine(IntPtr first, IntPtr second);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetNameFromIDList(IntPtr identifier, uint form, out IntPtr name);


    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(IntPtr menu);

    [LibraryImport("user32.dll", EntryPoint = "GetMenuStringW")]
    private static unsafe partial int GetMenuString(IntPtr menu, uint item, char* text, int maxCount, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetMenuItemID(IntPtr menu, int position);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetSubMenu(IntPtr menu, int position);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetMenuState(IntPtr menu, uint item, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "InsertMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InsertMenu(IntPtr menu, uint position, uint flags, UIntPtr newItemId, string? newItem);

    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenuEx(IntPtr menu, uint flags, int left, int top, IntPtr window, IntPtr parameters);
}