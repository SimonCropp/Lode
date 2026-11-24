/// <summary>
/// The list the taskbar shows when Lode's button is right-clicked, as Explorer's shows: the
/// folders pinned to Quick access under Pinned, then those Quick access adds because they are
/// used often under Frequent. Each opens in Lode, as a folder handed to it from anywhere does.
/// </summary>
/// <remarks>
/// The taskbar keeps the list for Lode's AppUserModelID (see TaskbarIdentity), so it shows
/// while Lode is closed too, on a pin of Lode. Lode writes it at the start, and again whenever
/// it reads Quick access anew. An app can only add sections of its own: Explorer's Pinned is
/// the taskbar's own Pinned section, which nobody but the user can add to. So Lode's Pinned is
/// a section like Frequent, and an entry pinned from the taskbar goes into a Pinned section of
/// the taskbar's, above Lode's, rather than into Quick access. An entry the user removes from
/// the list the taskbar reports the next time the list is written, and it is kept out from then
/// on, for as long as Quick access has it.
/// </remarks>
sealed class JumpList
{
    // System.Title, which the taskbar shows as an entry's name.
    static PropertyStore.PropertyKey titleKey = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    // SHGFI_ICONLOCATION: the file an icon is in, and its index there, rather than the icon.
    const uint iconLocationFlag = 0x1000;

    // SIID_DRIVENET: a drive mapped to a share.
    const uint stockNetworkDrive = 9;

    // FFFP_EXACTMATCH: the known folder at the path, not one the path is inside.
    const int exactMatch = 0;

    // INFOTIPSIZE, the most a shortcut's description holds.
    const int descriptionLength = 1024;

    // Where Windows describes its known folders, in the user's hive and the machine's, the
    // user's own first.
    const string folderDescriptions = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions";
    static RegistryKey[] hives = [Registry.CurrentUser, Registry.LocalMachine];

    Places places;
    Settings settings;
    SynchronizationContext context;

    // One writing at a time, the latest. The taskbar takes a list whole, but two being written
    // at once could each think the other's removed entries were never removed.
    Lock gate = new();

    // The folders the user removed from the list: the settings' at the start, then as each
    // writing finds. Only touched holding gate.
    List<string> removed;

    // Which writing is the latest asked for. One overtaken, by Quick access being read again
    // straight after, is dropped.
    int latest;

    JumpList(Places places, Settings settings)
    {
        this.places = places;
        this.settings = settings;
        context = new WindowsFormsSynchronizationContext();
        removed = settings.JumpListRemoved.ToList();
    }

    /// <summary>
    /// Writes the list now, from what Quick access had when it was last read, and again each
    /// time it is read anew. Made on the UI thread.
    /// </summary>
    public static void Follow(Places places, Settings settings)
    {
        var jumpList = new JumpList(places, settings);
        places.Changed += jumpList.Write;
        jumpList.Write();
    }

    // On a thread of its own, set up for the shell's COM objects as the UI thread is: the
    // icons are looked up as the list is written, and a folder's can take a moment. In the
    // background, so a slow shell does not keep Lode from exiting.
    void Write()
    {
        var version = Interlocked.Increment(ref latest);
        var pinned = places.Pinned;
        var frequent = places.Recent;
        var thread = new Thread(() => WriteIfLatest(version, pinned, frequent))
        {
            IsBackground = true,
            Name = "Lode jump list"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    void WriteIfLatest(int version, IReadOnlyList<string> pinned, IReadOnlyList<string> frequent)
    {
        List<string> remembered;
        lock (gate)
        {
            if (version != Volatile.Read(ref latest))
            {
                return;
            }

            try
            {
                Write(
                    TaskbarIdentity.AppId,
                    ExplorerReplacement.PreferredProgram(),
                    _ =>
                    {
                        removed = StillRemoved(removed, _.Removed, pinned, frequent);
                        return Plan(pinned, frequent, removed, _.Slots);
                    });
            }
            catch (Exception)
            {
                // The jump list is a nicety. The taskbar keeps the one written last.
            }

            remembered = removed.ToList();
        }

        try
        {
            context.Post(_ => settings.JumpListRemoved = remembered, null);
        }
        catch (InvalidOperationException)
        {
            // Lode is exiting, and there is no longer a UI thread to tell.
        }
    }

    /// <summary>
    /// What the taskbar says as a list is begun: how many entries it shows, and the folders of
    /// those the user removed from the list since it was last written.
    /// </summary>
    internal sealed record Started(int Slots, IReadOnlyList<string> Removed);

    internal sealed record Item(string Title, string Folder);

    internal sealed record Category(string Name, IReadOnlyList<Item> Items);

    /// <summary>
    /// What the list shows: Pinned, then Frequent, each in Quick access's order, without the
    /// folders the user removed from it, and only as many as the taskbar shows,
    /// <paramref name="slots"/> in all, the pinned ones first. Each is called what a tab showing
    /// it would be: its name, or where another entry has the same name, enough of its path to
    /// tell the two apart. A section with nothing in it is left out.
    /// </summary>
    internal static List<Category> Plan(
        IReadOnlyList<string> pinned,
        IReadOnlyList<string> frequent,
        IReadOnlyCollection<string> removed,
        int slots)
    {
        var pinnedShown = Shown(pinned, removed, slots);
        var frequentShown = Shown(frequent, removed, slots - pinnedShown.Count);
        var labels = Locations.Labels(pinnedShown.Concat(frequentShown));
        var categories = new List<Category>();
        Add("Pinned", pinnedShown);
        Add("Frequent", frequentShown);
        return categories;

        void Add(string name, List<string> folders)
        {
            if (folders.Count > 0)
            {
                categories.Add(new(name, folders.Select(_ => new Item(labels[_], _)).ToList()));
            }
        }
    }

    static List<string> Shown(IReadOnlyList<string> folders, IReadOnlyCollection<string> removed, int slots) =>
        folders
            .Where(_ => !removed.Contains(_, StringComparer.OrdinalIgnoreCase))
            .Take(Math.Max(slots, 0))
            .ToList();

    /// <summary>
    /// The folders to keep out of the list: those removed from it before, and those removed
    /// since it was last written, while Quick access still has them. One that leaves Quick
    /// access is forgotten, so it shows again if it comes back, pinned again say.
    /// </summary>
    internal static List<string> StillRemoved(
        IEnumerable<string> before,
        IEnumerable<string> since,
        IReadOnlyList<string> pinned,
        IReadOnlyList<string> frequent) =>
        before
            .Concat(since)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(_ => pinned.Contains(_, StringComparer.OrdinalIgnoreCase) ||
                        frequent.Contains(_, StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Writes the list for <paramref name="appId"/>, each entry starting
    /// <paramref name="program"/> with its folder. <paramref name="plan"/> is given what the
    /// taskbar said as the list was begun, and says what goes in it. Returns how each section
    /// went, as an HRESULT. One the taskbar refuses, as it does every section while Windows is
    /// set not to show recent items in jump lists, is left out, and the rest are still written.
    /// </summary>
    internal static List<(string Category, int Result)> Write(
        string appId,
        (string Program, string? Arguments) program,
        Func<Started, IReadOnlyList<Category>> plan)
    {
        var list = (ICustomDestinationList)new DestinationList();
        try
        {
            list.SetAppID(appId);
            list.BeginList(out var slots, typeof(IObjectArray).GUID, out var removedEntries);
            try
            {
                var results = new List<(string Category, int Result)>();
                foreach (var category in plan(new((int)slots, Folders(removedEntries))))
                {
                    var items = Collection(category.Items, program);
                    try
                    {
                        results.Add((category.Name, list.AppendCategory(category.Name, items)));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(items);
                    }
                }

                list.CommitList();
                return results;
            }
            catch
            {
                list.AbortList();
                throw;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    /// <summary>
    /// Takes the list for <paramref name="appId"/> off the taskbar.
    /// </summary>
    internal static void Delete(string appId)
    {
        var list = (ICustomDestinationList)new DestinationList();
        try
        {
            list.DeleteList(appId);
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    // The folders of the entries the taskbar says were removed: each entry's description,
    // which is its folder (see Link).
    static List<string> Folders(IObjectArray entries)
    {
        var folders = new List<string>();
        try
        {
            entries.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (entries.GetAt(i, typeof(ShellLink.IShellLinkW).GUID, out var entry) != 0)
                {
                    continue;
                }

                try
                {
                    if (Description((ShellLink.IShellLinkW)entry) is
                        {
                            Length: > 0
                        } folder)
                    {
                        folders.Add(folder);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(entry);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(entries);
        }

        return folders;
    }

    static string? Description(ShellLink.IShellLinkW link)
    {
        var buffer = Marshal.AllocCoTaskMem(descriptionLength * sizeof(char));
        try
        {
            link.GetDescription(buffer, descriptionLength);
            return Marshal.PtrToStringUni(buffer);
        }
        catch (COMException)
        {
            // Not one of Lode's entries, so not one it would add again.
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    static IObjectArray Collection(IEnumerable<Item> items, (string Program, string? Arguments) program)
    {
        var collection = (IObjectCollection)new EnumerableObjectCollection();
        foreach (var item in items)
        {
            var link = Link(item, program);
            try
            {
                collection.AddObject(link);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        return (IObjectArray)collection;
    }

    // An entry: a shortcut that starts Lode with the folder, named and with the icon Explorer's
    // entry for the folder has. Its description, the entry's tooltip, is the folder, as
    // Explorer's say where theirs are, and is how an entry the user removed is known again.
    static ShellLink.IShellLinkW Link(Item item, (string Program, string? Arguments) program)
    {
        var link = (ShellLink.IShellLinkW)new ShellLink.ShellLinkObject();
        link.SetPath(program.Program);
        var folder = Argument(item.Folder);
        link.SetArguments(program.Arguments == null ? folder : $"{program.Arguments} {folder}");
        link.SetDescription(item.Folder);
        if (IconLocation(item.Folder) is { } icon)
        {
            link.SetIconLocation(icon.File, icon.Index);
        }

        var store = (PropertyStore.IPropertyStore)link;
        PropertyStore.Set(store, titleKey, item.Title);
        store.Commit();
        return link;
    }

    /// <summary>
    /// <paramref name="folder"/> as one argument on a command line: in quotes, with any
    /// backslashes at its end doubled, since otherwise the last one escapes the closing quote,
    /// and D:\ arrives as D:".
    /// </summary>
    internal static string Argument(string folder)
    {
        var backslashes = folder.Length - folder.TrimEnd('\\').Length;
        return $"\"{folder}{new string('\\', backslashes)}\"";
    }

    /// <summary>
    /// Where the icon an entry shows for <paramref name="folder"/> is, as a shortcut names one:
    /// the file, and the icon's index in it (a resource id when negative). A known folder's own,
    /// Desktop's or Documents', as Windows registers it; then the one the shell gives the folder,
    /// a drive's, or one its desktop.ini names; then Windows' folder icon. A folder on the
    /// network isn't asked, since that could wait on the network: a share, a machine and a
    /// mapped drive get Windows' icons for them, and a folder in one the folder icon.
    /// </summary>
    internal static (string File, int Index)? IconLocation(string folder)
    {
        if (Locations.IsRemote(folder))
        {
            return TaskbarIdentity.StockIcon(RemoteIcon(folder));
        }

        return KnownFolderIcon(folder) ??
               ShellIcon(folder) ??
               TaskbarIdentity.StockIcon(TaskbarIdentity.StockFolder);
    }

    static uint RemoteIcon(string folder)
    {
        if (Locations.IsServer(folder))
        {
            return TaskbarIdentity.StockServer;
        }

        if (Locations.IsShare(folder))
        {
            return TaskbarIdentity.StockServerShare;
        }

        if (!Locations.IsNetwork(folder) &&
            Locations.Same(Path.GetPathRoot(folder), folder))
        {
            return stockNetworkDrive;
        }

        return TaskbarIdentity.StockFolder;
    }

    static (string File, int Index)? KnownFolderIcon(string folder)
    {
        Guid id;
        try
        {
            var manager = (IKnownFolderManager)new KnownFolderManager();
            try
            {
                if (manager.FindFolderFromPath(folder, exactMatch, out var known) != 0)
                {
                    return null;
                }

                try
                {
                    known.GetId(out id);
                }
                finally
                {
                    Marshal.ReleaseComObject(known);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (COMException)
        {
            return null;
        }

        foreach (var hive in hives)
        {
            using var description = hive.OpenSubKey($@"{folderDescriptions}\{id:B}");
            if (description?.GetValue("Icon") is string { Length: > 0 } icon)
            {
                return ParseIconLocation(icon);
            }
        }

        return null;
    }

    static (string File, int Index)? ShellIcon(string folder)
    {
        var info = new ShellFileInfo();
        if (SHGetFileInfo(folder, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), iconLocationFlag) == IntPtr.Zero)
        {
            return null;
        }

        return Existing(info.DisplayName, info.IconIndex);
    }

    /// <summary>
    /// An icon location as the registry gives one, "file,index", with the index 0 when there is
    /// none. Null if the file isn't there.
    /// </summary>
    internal static (string File, int Index)? ParseIconLocation(string location)
    {
        var separator = location.LastIndexOf(',');
        if (separator >= 0 &&
            int.TryParse(location.AsSpan(separator + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            return Existing(location[..separator], index);
        }

        return Existing(location, 0);
    }

    // A shortcut names its icon's file whole, and the taskbar shows a blank icon for one that
    // isn't there, worse than the folder icon.
    static (string File, int Index)? Existing(string file, int index)
    {
        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (file.Length == 0 ||
            !File.Exists(file))
        {
            return null;
        }

        return (file, index);
    }

    [ComImport]
    [Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    class DestinationList;

    [ComImport]
    [Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")]
    class EnumerableObjectCollection;

    [ComImport]
    [Guid("4df0c730-df9d-4ae3-9153-aa6b82e9795a")]
    class KnownFolderManager;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void BeginList(
            out uint minimumSlots,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IObjectArray removed);

        [PreserveSig]
        int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);

        void AppendKnownCategory(int category);

        void AddUserTasks(IObjectArray tasks);

        void CommitList();

        void GetRemovedDestinations(
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IObjectArray removed);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);

        [PreserveSig]
        int AbortList();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
    interface IObjectArray
    {
        void GetCount(out uint count);

        [PreserveSig]
        int GetAt(
            uint index,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object item);
    }

    // IObjectArray's methods come first, as they do in its vtable.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
    interface IObjectCollection
    {
        void GetCount(out uint count);

        [PreserveSig]
        int GetAt(
            uint index,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object item);

        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object item);

        void AddFromArray(IObjectArray source);

        void RemoveObjectAt(uint index);

        void Clear();
    }

    // Only as far as FindFolderFromPath, the one used; the rest are there for the vtable.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8BE2D872-86AA-4d47-B776-32CCA40C7018")]
    interface IKnownFolderManager
    {
        void FolderIdFromCsidl(int csidl, out Guid id);

        void FolderIdToCsidl(ref Guid id, out int csidl);

        void GetFolderIds(out IntPtr ids, out uint count);

        void GetFolder(ref Guid id, out IKnownFolder folder);

        void GetFolderByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IKnownFolder folder);

        void RegisterFolder(ref Guid id, IntPtr definition);

        void UnregisterFolder(ref Guid id);

        [PreserveSig]
        int FindFolderFromPath([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, out IKnownFolder folder);
    }

    // Only GetId, the first, is used.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3AA7AF7E-9B36-420c-A8E3-F77D4674A488")]
    interface IKnownFolder
    {
        void GetId(out Guid id);
    }
}
