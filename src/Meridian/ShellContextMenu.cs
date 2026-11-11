/// <summary>
/// Shows the classic Windows shell context menu (the IContextMenu one, with every installed
/// shell extension) for files, folders, or a folder's background.
/// </summary>
sealed class ShellContextMenu : NativeWindow, IDisposable
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

        customAction?.Invoke();
    }

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
    /// Takes out the entries Meridian leaves off: those in <see cref="removedVerbs"/>, plus
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

    // What Explorer calls each item in a folder, such as "Mail recipient" for
    // Mail Recipient.MAPIMail.
    static HashSet<string> DisplayNames(string folder)
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

    static string MenuText(IntPtr menu, int position)
    {
        var text = new StringBuilder(256);
        GetMenuString(menu, (uint)position, text, text.Capacity, menuByPosition);
        return text.ToString();
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

    public record MenuEntry(string Text, string? Verb, IReadOnlyList<MenuEntry> Submenu);

    /// <summary>
    /// The top level of the menu the shell builds for the items, as Meridian would show it.
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
            var text = new StringBuilder(256);
            GetMenuString(menu, (uint)position, text, text.Capacity, menuByPosition);
            var id = GetMenuItemID(menu, position);
            var verb = id is >= firstShellCommand and <= lastShellCommand ? GetVerb(contextMenu, id - firstShellCommand) : null;
            var submenu = GetSubMenu(menu, position);
            entries.Add(new(text.ToString(), verb, submenu == IntPtr.Zero ? [] : DescribeMenu(contextMenu, submenu)));
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

    [StructLayout(LayoutKind.Sequential)]
    struct InvokeCommandInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Title;
        public IntPtr VerbUnicode;
        public IntPtr ParametersUnicode;
        public IntPtr DirectoryUnicode;
        public IntPtr TitleUnicode;
        public Point InvokePoint;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr owner, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eaten, out IntPtr identifier, IntPtr attributes);

        [PreserveSig]
        int EnumObjects(IntPtr owner, int flags, out IntPtr enumerator);

        [PreserveSig]
        int BindToObject(IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int BindToStorage(IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);

        [PreserveSig]
        int CreateViewObject(IntPtr owner, ref Guid interfaceId, out IntPtr result);

        [PreserveSig]
        int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] identifiers, ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] identifiers, ref Guid interfaceId, IntPtr reserved, out IntPtr result);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr identifier, uint flags, IntPtr name);

        [PreserveSig]
        int SetNameOf(IntPtr owner, IntPtr identifier, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr result);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig]
        int InvokeCommand(ref InvokeCommandInfo info);

        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint length);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig]
        int InvokeCommand(ref InvokeCommandInfo info);

        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint length);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig]
        int InvokeCommand(ref InvokeCommandInfo info);

        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint length);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr identifier, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr identifier, ref Guid interfaceId, out IntPtr parent, out IntPtr child);

    [DllImport("shell32.dll")]
    static extern int SHBindToObject(IntPtr parent, IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [DllImport("shell32.dll")]
    static extern IntPtr ILFindLastID(IntPtr identifier);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMenuString(IntPtr menu, uint item, StringBuilder? text, int maxCount, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll")]
    static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll")]
    static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool InsertMenu(IntPtr menu, uint position, uint flags, UIntPtr newItemId, string? newItem);

    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int left, int top, IntPtr window, IntPtr parameters);
}
