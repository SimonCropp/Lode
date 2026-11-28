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
    const uint invokeNoAsync = 0x100;
    const uint invokePointValid = 0x20000000;
    const uint invokeShiftDown = 0x10000000;
    const uint invokeControlDown = 0x40000000;
    const int showNormal = 1;

    static Guid shellFolderId = new("000214E6-0000-0000-C000-000000000046");
    static Guid contextMenuId = new("000214E4-0000-0000-C000-000000000046");
    static Guid dataObjectId = new("0000010E-0000-0000-C000-000000000046");

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
        Func<string, bool> handleVerb,
        bool canRename = true)
    {
        var contextMenu = CreateForItems(owner, paths);
        if (contextMenu == null)
        {
            return false;
        }

        Show(contextMenu, owner, location, customCommands, customFirst: false, RenameFlag(canRename), handleVerb, ItemsFolder(paths));
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
        Func<string, bool> handleVerb,
        bool canRename = true)
    {
        var contextMenu = CreateForQuickAccessItem(owner, path);
        if (contextMenu == null)
        {
            return ShowForItems(owner, [path], location, customCommands, handleVerb, canRename);
        }

        Show(contextMenu, owner, location, customCommands, customFirst: false, RenameFlag(canRename), handleVerb, ItemsFolder([path]));
        return true;
    }

    // CMF_CANRENAME tells the shell the host renames, as the rename verb then comes to it, so
    // it offers Rename. Only where the host does: with nothing to do the renaming, the verb
    // would go nowhere.
    static uint RenameFlag(bool canRename)
    {
        if (canRename)
        {
            return canRenameFlag;
        }

        return 0;
    }

    // The shell folder behind Explorer's Quick access list.
    const string quickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
    // A folder's path, or for a machine on the network, which has none in the file system,
    // its \\server name, as Quick access gives it.
    const uint parsingName = 0x80028000;

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
            if (SHGetNameFromIDList(full, parsingName, out var name) != 0)
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

        Show(contextMenu, owner, location, customCommands, customFirst: true, 0, handleVerb, FileSystemFolder(folder));
        return true;
    }

    /// <summary>
    /// Shows This PC's own menu, as the shell gives it for This PC on the desktop, less Delete
    /// and Rename, which Lode leaves off This PC.
    /// </summary>
    public bool ShowForThisPC(
        IntPtr owner,
        Point location,
        IReadOnlyList<CustomCommand> customCommands,
        Func<string, bool> handleVerb)
    {
        var contextMenu = CreateForItems(owner, [Locations.ThisPCParsingName]);
        if (contextMenu == null)
        {
            return false;
        }

        // Not asked for Rename, the shell leaves it off; Delete it has to be told to.
        Show(contextMenu, owner, location, customCommands, customFirst: false, 0, handleVerb, null, thisPCRemovedVerbs);
        return true;
    }

    static HashSet<string> thisPCRemovedVerbs =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "delete",
        "rename"
    ];

    /// <summary>
    /// Shows the items' properties. Items of one folder are that folder's to show them for, by
    /// its menu's Properties. Those of more than one, as a search finds, have no folder to ask,
    /// so the shell's properties for several items are given them all, each named in full.
    /// </summary>
    public static void ShowProperties(IntPtr owner, IReadOnlyList<string> paths)
    {
        if (InOneFolder(paths))
        {
            InvokeVerb(owner, paths, "properties");
            return;
        }

        var dataObject = CreateDataObjectAcrossFolders(paths);
        if (dataObject == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SHMultiFileProperties(dataObject, 0);
        }
        finally
        {
            Marshal.Release(dataObject);
        }
    }

    static Guid dataObjectHandler = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");

    /// <summary>
    /// The shell's data object for items wherever they are, each named in full rather than to
    /// the folder it is in. Zero when the shell knows none of them; otherwise the caller
    /// releases it.
    /// </summary>
    internal static IntPtr CreateDataObjectAcrossFolders(IReadOnlyList<string> paths)
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

            if (identifiers.Count == 0 ||
                SHCreateShellItemArrayFromIDLists((uint)identifiers.Count, [.. identifiers], out var itemsPointer) != 0)
            {
                return IntPtr.Zero;
            }

            var items = (IShellItemArray)Marshal.GetObjectForIUnknown(itemsPointer);
            Marshal.Release(itemsPointer);
            try
            {
                var handler = dataObjectHandler;
                var interfaceId = dataObjectId;
                if (items.BindToHandler(IntPtr.Zero, ref handler, ref interfaceId, out var dataObject) != 0)
                {
                    return IntPtr.Zero;
                }

                return dataObject;
            }
            finally
            {
                Marshal.ReleaseComObject(items);
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

        InvokeVerb(contextMenu, owner, verb, directory: ItemsFolder(paths));
    }

    /// <summary>
    /// Runs a verb of a folder's empty area, such as Add a network location on This PC's,
    /// without showing a menu.
    /// </summary>
    public static void InvokeFolderVerb(IntPtr owner, string folder, string verb)
    {
        var contextMenu = CreateForFolder(owner, folder);
        if (contextMenu == null)
        {
            return;
        }

        InvokeVerb(contextMenu, owner, verb, directory: FileSystemFolder(folder));
    }

    /// <summary>
    /// Pins a folder to Quick access, or unpins it, as the shell's own menu entries do. Done off
    /// the UI thread, as <see cref="MoveToEnd"/> is: pinning reads all of Quick access, to see
    /// whether the folder is pinned already, which asks the shell about every folder it has,
    /// and that can wait on the network, with every window waiting were it on the UI thread.
    /// Quick access is read again once it is done, back on the calling thread, when the task
    /// completes.
    /// </summary>
    public static Task SetPinned(IntPtr owner, string path, bool pin) =>
        TakeTurn("Lode pinning", () => ChangePinned(owner, path, pin));

    /// <summary>
    /// Pins the folder or unpins it, on the thread <see cref="SetPinned"/> does it on. Tests
    /// swap it, which must not change the machine's Quick access.
    /// </summary>
    internal static Action<IntPtr, string, bool> ChangePinned = (owner, path, pin) =>
    {
        if (pin)
        {
            Pin(path);
        }
        else
        {
            Unpin(owner, path);
        }
    };

    /// <summary>
    /// Moves pinned folders to the end of the Pinned list one after another, so they end up
    /// last in this order: each is unpinned and pinned again, which puts it at the end. Quick
    /// access is read again once, after them all, back on the calling thread, when the task
    /// completes. The shell shows its busy cursor while it pins or unpins, which on the UI
    /// thread made the pointer flicker twice for each folder moved, so they are moved off it.
    /// </summary>
    public static Task MoveToEnd(IntPtr owner, IReadOnlyList<string> folders) =>
        TakeTurn("Lode pinned order", () => MoveEachToEnd(owner, folders));

    // The pin, unpin or move asked for last, done once it has been. They take turns, in the
    // order they were asked for: pinning works out from Quick access whether the folder is
    // pinned already, and a move from the order it has, which another running alongside would
    // change under it. Folders dropped on Pinned are pinned, then those after where they were
    // dropped moved after them, which must wait for the pins.
    static Task lastTurn = Task.CompletedTask;

    // Does the work once what was asked for before it is done, on a thread of its own, set up
    // for the shell's COM objects as the UI thread is, and in the background, so a slow shell
    // does not keep Lode from exiting. Quick access is then read again, back on the calling
    // thread, when the task completes.
    static Task TakeTurn(string name, Action work)
    {
        var context = SynchronizationContext.Current ?? new SynchronizationContext();
        var finished = new TaskCompletionSource();
        var before = Interlocked.Exchange(ref lastTurn, finished.Task);
        var done = new TaskCompletionSource();
        var thread = new Thread(
            () =>
            {
                try
                {
                    before.Wait();
                    work();
                }
                catch (Exception)
                {
                    // Left as far as it got. Quick access is read again regardless, so the
                    // tree shows that.
                }
                finally
                {
                    finished.SetResult();
                }

                try
                {
                    context.Post(
                        _ =>
                        {
                            QuickAccessChanged?.Invoke();
                            done.SetResult();
                        },
                        null);
                }
                catch (InvalidOperationException)
                {
                    // Lode is exiting, and there is no longer a UI thread to tell.
                }
            })
        {
            IsBackground = true,
            Name = name
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    static void MoveEachToEnd(IntPtr owner, IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            // One the shell can't name now, on a drive since unplugged, or a share out of
            // reach, could be unpinned but not pinned again: left pinned where it is, rather
            // than lost from Quick access for being moved.
            if (!CanPin(folder))
            {
                continue;
            }

            // Pinning toggles, so a folder is pinned again only once it has been unpinned. Were
            // pinning to fail, the folders after it are left pinned where they are rather than
            // unpinned too.
            if (Unpin(owner, folder) &&
                !TogglePinned(folder))
            {
                return;
            }
        }
    }

    // Whether the shell can name the folder, which is the first thing pinning it asks.
    static bool CanPin(string path)
    {
        var itemId = shellItemId;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item) != 0)
        {
            return false;
        }

        Marshal.Release(item);
        return true;
    }

    // Unpin is only offered by the folder's Quick access entry, so it is asked for that. True
    // when it ran: false when Quick access has no entry for the folder, or one not pinned.
    static bool Unpin(IntPtr owner, string path)
    {
        var contextMenu = CreateForQuickAccessItem(owner, path);
        if (contextMenu == null)
        {
            return false;
        }

        // Done by the time it returns, so the folder can be pinned again straight after.
        return InvokeVerb(contextMenu, owner, "unpinfromhome", invokeNoAsync) >= 0;
    }

    // Unlike Unpin, Pin to Quick access is not asked of the folder's menu: some versions of
    // Windows leave it off folders' menus, though the command behind it still pins them. That
    // command toggles, unpinning a folder pinned already, so it only runs on a folder Quick
    // access does not have pinned.
    static void Pin(string path)
    {
        // Read once for folders pinned one straight after another, several dropped together
        // say: reading Quick access asks the shell for all of it, and looks for each folder on
        // disk. Pins take turns, so no other is reading or changing this meanwhile.
        var now = Environment.TickCount64;
        if (pinnedRead is not { } read ||
            now - read.At > 2000)
        {
            read = (now, [.. Places.QuickAccessFolders().Pinned]);
            pinnedRead = read;
        }

        if (read.Pinned.Any(_ => Locations.Same(_, path)))
        {
            return;
        }

        if (TogglePinned(path))
        {
            read.Pinned.Add(path);
        }
    }

    // What Quick access had pinned when it was last read to pin a folder, and when that was.
    static (long At, List<string> Pinned)? pinnedRead;

    // What the shell's Pin to Quick access entries run, for folders, drives and files alike.
    static Guid pinToQuickAccessCommand = new("b455f46e-e4af-4035-b0a4-cf18d2f6f28e");
    static Guid shellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    static Guid shellItemArrayId = new("b63ea76d-1f85-456f-a19c-48159efa858b");

    // Pins the folder, or unpins it if it is pinned. True when the command ran.
    static bool TogglePinned(string path)
    {
        var itemId = shellItemId;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item) != 0)
        {
            return false;
        }

        var items = IntPtr.Zero;
        object? command = null;
        try
        {
            var itemsId = shellItemArrayId;
            if (SHCreateShellItemArrayFromShellItem(item, ref itemsId, out items) != 0)
            {
                return false;
            }

            command = Activator.CreateInstance(Type.GetTypeFromCLSID(pinToQuickAccessCommand)!);
            if (command is not IObjectWithSelection selection ||
                command is not IExecuteCommand execute ||
                selection.SetSelection(items) < 0)
            {
                return false;
            }

            execute.SetShowWindow(showNormal);
            return execute.Execute() >= 0;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (command != null)
            {
                Marshal.ReleaseComObject(command);
            }

            if (items != IntPtr.Zero)
            {
                Marshal.Release(items);
            }

            Marshal.Release(item);
        }
    }

    static int InvokeVerb(IContextMenu contextMenu, IntPtr owner, string verb, uint flags = 0, string? directory = null)
    {
        var menu = CreatePopupMenu();
        var verbAnsi = Marshal.StringToHGlobalAnsi(verb);
        var verbUnicode = Marshal.StringToHGlobalUni(verb);
        using var workingDirectory = new WorkingDirectory(directory);
        try
        {
            // The whole menu is built to run the one verb, so a stuck extension would hold up
            // Properties, or an unpin, as it would the menu shown.
            using (new ShellExtensionGuard())
            {
                contextMenu.QueryContextMenu(menu, 0, firstShellCommand, lastShellCommand, 0);
            }

            var info = new InvokeCommandInfo
            {
                Size = Marshal.SizeOf<InvokeCommandInfo>(),
                Mask = invokeUnicode | flags,
                Window = owner,
                Verb = verbAnsi,
                VerbUnicode = verbUnicode,
                Directory = workingDirectory.Ansi,
                DirectoryUnicode = workingDirectory.Unicode,
                Show = showNormal
            };
            return contextMenu.InvokeCommand(ref info);
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
        Func<string, bool> handleVerb,
        string? directory,
        IReadOnlySet<string>? alsoRemoved = null)
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

            // A stuck extension would otherwise hold up the menu, and the window with it.
            using (new ShellExtensionGuard())
            {
                if (contextMenu.QueryContextMenu(menu, 0, firstShellCommand, lastShellCommand, flags) < 0)
                {
                    return;
                }
            }

            RemoveUnwanted(contextMenu, menu, alsoRemoved);
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
                    Invoke(contextMenu, owner, offset, location, directory);
                    if (verb != null &&
                        quickAccessVerbs.Contains(verb))
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

    static HashSet<string> quickAccessVerbs =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "pintohome",
        "unpinfromhome"
    ];

    // Entries left out of every menu, by verb, which unlike the text is the same in every
    // language.
    static HashSet<string> removedVerbs =
    [
        with(StringComparer.OrdinalIgnoreCase),
        // Restore previous versions
        "PreviousVersions",
        // Edit in Notepad, added by the Notepad app
        "{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"
    ];

    /// <summary>
    /// Takes out the entries Lode leaves off: those in <see cref="removedVerbs"/>, plus
    /// Send to and Include in library. Those two are submenus with no verb, so they are known
    /// by what they list: the SendTo folder, and the user's libraries. Those with a verb in
    /// <paramref name="alsoRemoved"/> go too, for what Lode leaves off this menu alone.
    /// </summary>
    static void RemoveUnwanted(IContextMenu contextMenu, IntPtr menu, IReadOnlySet<string>? alsoRemoved = null)
    {
        var sendTo = DisplayNames(Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        var libraries = DisplayNames(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Libraries"));
        if (sendTo.Count > 0 ||
            libraries.Count > 0)
        {
            AwaitFilling(menu, sendTo, libraries);
        }

        for (var position = GetMenuItemCount(menu) - 1; position >= 0; position--)
        {
            var submenu = GetSubMenu(menu, position);
            bool remove;
            if (submenu != IntPtr.Zero)
            {
                remove = Lists(submenu, sendTo) ||
                         Lists(submenu, libraries);
            }
            else
            {
                var id = GetMenuItemID(menu, position);
                remove = id is >= firstShellCommand and <= lastShellCommand &&
                         GetVerb(contextMenu, id - firstShellCommand) is { } verb &&
                         (removedVerbs.Contains(verb) ||
                          alsoRemoved?.Contains(verb) == true);
            }

            if (remove)
            {
                DeleteMenu(menu, (uint)position, menuByPosition);
            }
        }

        TidySeparators(menu);
    }

    // The longest the shell is given to fill in the menu's submenus, all of them together.
    const int fillTimeout = 500;

    /// <summary>
    /// Send to and Include in library can only be known by what they list, and the shell fills
    /// both in after the menu is built, on messages posted back to this thread: Send to holds a
    /// blank entry until then, then one entry at a time, Include in library a greyed "Retrieving
    /// libraries...". So the shell is let fill those in, until each lists more than one entry or
    /// one it is known by, for <see cref="fillTimeout"/> at most for the whole menu. Other
    /// submenus hold one entry too: Open with and Cast to Device one of their own name, which
    /// the shell fills in only once the submenu is opened, so they are not waited on: they would
    /// only run out the time, on every image's menu.
    /// </summary>
    static void AwaitFilling(IntPtr menu, HashSet<string> sendTo, HashSet<string> libraries)
    {
        var filling = new List<IntPtr>();
        for (var position = 0; position < GetMenuItemCount(menu); position++)
        {
            var submenu = GetSubMenu(menu, position);
            if (submenu != IntPtr.Zero &&
                HoldsPlaceholder(submenu))
            {
                filling.Add(submenu);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        while (filling.Count > 0)
        {
            if (!RunPosted())
            {
                return;
            }

            filling.RemoveAll(
                _ => GetMenuItemCount(_) != 1 ||
                     Lists(_, sendTo) ||
                     Lists(_, libraries));
            var remaining = fillTimeout - stopwatch.ElapsedMilliseconds;
            if (filling.Count == 0 ||
                remaining <= 0)
            {
                return;
            }

            MsgWaitForMultipleObjects(0, IntPtr.Zero, false, (uint)remaining, postedOrSent);
        }
    }

    // MF_GRAYED and MF_DISABLED.
    const uint menuUnavailable = 0x3;

    // One entry that can't be chosen, as the placeholders the shell fills in on its own are:
    // blank, as Send to's is, or greyed, as Include in library's is. Not known by its command:
    // a placeholder has one in the range the handler was given, as any entry has. One that can
    // be chosen, as Open with's can, waits for the submenu to be opened.
    static bool HoldsPlaceholder(IntPtr submenu)
    {
        if (GetMenuItemCount(submenu) != 1 ||
            GetSubMenu(submenu, 0) != IntPtr.Zero)
        {
            return false;
        }

        return MenuText(submenu, 0).Length == 0 ||
               (GetMenuState(submenu, 0, menuByPosition) & menuUnavailable) != 0;
    }

    const uint removeMessage = 0x1;
    // PM_QS_POSTMESSAGE and PM_QS_SENDMESSAGE: posted messages, timers and hot keys, and those
    // sent from other threads.
    const uint postedOrSentMessages = 0x00D80000;
    // QS_POSTMESSAGE, QS_TIMER, QS_HOTKEY and QS_SENDMESSAGE, what wakes a wait for those.
    const uint postedOrSent = 0xD8;
    const uint quitMessage = 0x12;

    /// <summary>
    /// Runs what has been posted to this thread, as the shell's fill-ins are, but not input,
    /// which Application.DoEvents would run too: a second right-click, or a key, waits for this
    /// menu rather than starting on another while this one is still being built. False on
    /// WM_QUIT, which is left for the message loop.
    /// </summary>
    static bool RunPosted()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, removeMessage | postedOrSentMessages))
        {
            if (message.Message == quitMessage)
            {
                PostQuitMessage((int)message.WParam);
                return false;
            }

            TranslateMessage(in message);
            DispatchMessage(in message);
        }

        return true;
    }

    // MSG.
    [StructLayout(LayoutKind.Sequential)]
    struct WindowMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    // Asking the shell for every name took most of the time this class adds to a right-click, so
    // each folder's names are kept, and read again only once the folder has changed.
    static ConcurrentDictionary<string, (DateTime Changed, HashSet<string> Names)> namesByFolder =
        new(StringComparer.OrdinalIgnoreCase);

    // What Explorer calls each item in a folder, such as "Mail recipient" for
    // Mail Recipient.MAPIMail.
    static HashSet<string> DisplayNames(string folder)
    {
        var changed = Directory.GetLastWriteTimeUtc(folder);
        if (namesByFolder.TryGetValue(folder, out var cached) &&
            cached.Changed == changed)
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

    static bool Lists(IntPtr submenu, HashSet<string> names) =>
        Lists(
            Enumerable.Range(0, GetMenuItemCount(submenu))
                .Select(_ => MenuText(submenu, _))
                .ToList(),
            names);

    // True when a submenu's entries list those names. One entry may be something else, such as
    // Include in library's "Create new library". Drives are left out: Windows adds removable and
    // mapped ones to Send to, which are not in the SendTo folder, and there can be any number.
    internal static bool Lists(IReadOnlyList<string> entries, IReadOnlySet<string> names)
    {
        if (names.Count == 0)
        {
            return false;
        }

        var texts = entries
            .Select(_ => _.Replace("&", ""))
            .Where(_ => _.Length > 0 &&
                        !IsDrive(_))
            .ToList();
        return texts.Count > 0 &&
               texts.Count(names.Contains) >= Math.Max(1, texts.Count - 1);
    }

    // Ends in a drive letter, as "USB Drive (E:)" or "Work (\server) (Z:)" does.
    static bool IsDrive(string text) =>
        text is [.., '(', var letter, ':', ')'] &&
        char.IsAsciiLetter(letter);

    static unsafe string MenuText(IntPtr menu, int position)
    {
        const int capacity = 256;
        var text = stackalloc char[capacity];
        var length = GetMenuString(menu, (uint)position, text, capacity, menuByPosition);
        return new(text, 0, Math.Max(0, length));
    }

    // For an entry that opens a submenu, GetMenuState gives how many entries that has in the
    // high byte, where 8 to 15 of them set the separator's bit. So an entry with a submenu is
    // never a separator.
    static bool IsSeparator(IntPtr menu, int position) =>
        GetSubMenu(menu, position) == IntPtr.Zero &&
        (GetMenuState(menu, (uint)position, menuByPosition) & menuSeparator) != 0;

    // Removes separators left at either end, or next to each other, by removed entries.
    internal static void TidySeparators(IntPtr menu)
    {
        for (var position = GetMenuItemCount(menu) - 1; position >= 0; position--)
        {
            var last = position == GetMenuItemCount(menu) - 1;
            if (IsSeparator(menu, position) &&
                (position == 0 ||
                 last ||
                 IsSeparator(menu, position + 1)))
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

    /// <summary>
    /// Whether the first of Lode's own menus to open has the shell's menu warmed up behind it.
    /// Set by the app; tests leave it off, and load no shell extensions they did not ask for.
    /// </summary>
    public static bool WarmsUpBehindMenus { get; set; }

    static int warmUpStarted;

    /// <summary>
    /// One of Lode's own menus has opened, which is the first sign the shell's might be wanted:
    /// Show more options is a click away. The first time, <see cref="WarmUp"/> runs behind it.
    /// Until then nothing of the shell's is loaded, and someone who never opens a menu never
    /// pays for it. The extensions are loaded into the process whichever thread asks, so this
    /// one is its own: set up for the shell's COM objects as the UI thread is, so the menu
    /// being read is not held up, and in the background, so an extension that never returns
    /// does not keep Lode from exiting.
    /// </summary>
    public static void MenuOpened()
    {
        if (!WarmsUpBehindMenus ||
            Interlocked.Exchange(ref warmUpStarted, 1) != 0)
        {
            return;
        }

        var thread = new Thread(WarmUp)
        {
            IsBackground = true,
            Name = "Lode shell menu warm-up"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public record MenuEntry(string Text, string? Verb, IReadOnlyList<MenuEntry> Submenu);

    /// <summary>
    /// The top level of the menu the shell builds for the items, as Lode would show it, for a
    /// host that renames or one that doesn't.
    /// </summary>
    public static List<MenuEntry> Describe(IntPtr owner, IReadOnlyList<string> paths, bool canRename = true)
    {
        var contextMenu = CreateForItems(owner, paths);
        if (contextMenu == null)
        {
            return [];
        }

        var menu = CreatePopupMenu();
        try
        {
            using (new ShellExtensionGuard())
            {
                contextMenu.QueryContextMenu(menu, 0, firstShellCommand, lastShellCommand, RenameFlag(canRename));
            }

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

    // The folder the items are in, which what a verb starts works in, as Explorer has it:
    // given none, a program would get Lode's own, System32. None for what is not on disk.
    static string? ItemsFolder(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 ||
            FileSystemFolder(paths[0]) is not { } path)
        {
            return null;
        }

        // A drive is in no folder: it is its own.
        return Path.GetDirectoryName(path) ?? path;
    }

    static string? FileSystemFolder(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return null;
    }

    // A folder for a verb to start in, as the ANSI and Unicode strings InvokeCommand is given,
    // each zero for none.
    sealed class WorkingDirectory(string? path) : IDisposable
    {
        public IntPtr Ansi { get; } = path == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(path);
        public IntPtr Unicode { get; } = path == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(path);

        public void Dispose()
        {
            Marshal.FreeHGlobal(Ansi);
            Marshal.FreeHGlobal(Unicode);
        }
    }

    static void Invoke(IContextMenu contextMenu, IntPtr owner, uint offset, Point location, string? directory)
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

        using var workingDirectory = new WorkingDirectory(directory);
        var info = new InvokeCommandInfo
        {
            Size = Marshal.SizeOf<InvokeCommandInfo>(),
            Mask = mask,
            Window = owner,
            Verb = (IntPtr)offset,
            VerbUnicode = (IntPtr)offset,
            Directory = workingDirectory.Ansi,
            DirectoryUnicode = workingDirectory.Unicode,
            Show = showNormal,
            InvokePoint = location
        };
        contextMenu.InvokeCommand(ref info);
    }

    static IContextMenu? CreateForItems(IntPtr owner, IReadOnlyList<string> paths)
    {
        var pointer = CreateForItems(owner, paths, contextMenuId);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        return Wrap(pointer);
    }

    /// <summary>
    /// The shell's data object for items that share a parent folder, as dragging them carries:
    /// how an app is handed files to open. Zero when the shell does not know them; otherwise
    /// the caller releases it.
    /// </summary>
    public static IntPtr CreateDataObject(IntPtr owner, IReadOnlyList<string> paths) =>
        CreateForItems(owner, paths, dataObjectId);

    // What the items' folder offers for them, as the interface asked for, or zero when it
    // offers nothing. The caller releases it.
    static IntPtr CreateForItems(IntPtr owner, IReadOnlyList<string> paths, Guid interfaceId)
    {
        // Each item is named to the folder by its own name alone, so they must all be in the one
        // folder, as a search's items need not be: named to the first one's, the others would
        // name whatever is there by their names, if anything.
        if (!InOneFolder(paths))
        {
            return IntPtr.Zero;
        }

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
                return IntPtr.Zero;
            }

            var folderId = shellFolderId;
            if (SHBindToParent(identifiers[0], ref folderId, out var folderPointer, out _) != 0)
            {
                return IntPtr.Zero;
            }

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            try
            {
                var children = identifiers.Select(ILFindLastID).ToArray();
                var result = folder.GetUIObjectOf(
                    owner,
                    (uint)children.Length,
                    children,
                    ref interfaceId,
                    IntPtr.Zero,
                    out var pointer);
                if (result != 0)
                {
                    return IntPtr.Zero;
                }

                return pointer;
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

    static bool InOneFolder(IReadOnlyList<string> paths) =>
        !paths
            .Select(_ => Path.GetDirectoryName(_) ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();

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

            var folder = (IShellFolder) Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            try
            {
                var menuId = contextMenuId;
                var result = folder.CreateViewObject(owner, ref menuId, out var menuPointer);
                if (result == 0)
                {
                    return Wrap(menuPointer);
                }

                return null;
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
            else if (menu2 != null &&
                     message.Msg != menuCharMessage)
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

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IntPtr item);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateShellItemArrayFromShellItem(IntPtr item, ref Guid interfaceId, out IntPtr items);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] identifiers, out IntPtr items);

    [LibraryImport("shell32.dll")]
    private static partial int SHMultiFileProperties(IntPtr dataObject, uint flags);


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

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out WindowMessage message, IntPtr window, uint first, uint last, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in WindowMessage message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(in WindowMessage message);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjects(uint count, IntPtr handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, uint wakeMask);
}