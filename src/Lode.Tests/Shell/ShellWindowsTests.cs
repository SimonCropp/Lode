/// <summary>
/// Tabs in the shell's list of folder windows, where an app asking for a file shown selected (GitHub
/// Desktop's Show in Explorer, say) finds the one showing its folder. Against a list of the
/// tests' own, rather than Explorer's.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class ShellWindowsTests
{
    // SVSIF: what SHOpenFolderAndSelectItems passes to SelectItem, for the first item and for
    // each after it.
    const uint selectFirst = 0x1D;
    const uint selectAnother = 0x1;

    [Test]
    public async Task TabsOfAShowingWindowAreInTheList()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        list.Name(form, "window");
        var registered = list.Describe();

        var source = form.Views.First();
        source.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(source);
        var navigated = list.Describe();

        form.CloseTabsToTheRight((TabPage)source.Parent!);

        await Verify(
            new
            {
                registered,
                navigated,
                closed = list.Describe(),
                calls = list.Calls
            })
            .Snapshot(
                """
                {
                  registered: [
                    window: Source,
                    window: Docs
                  ],
                  navigated: [
                    window: Source\Deep,
                    window: Docs
                  ],
                  closed: [
                    window: Source\Deep
                  ],
                  calls: [
                    RegisterPending 1 Source, class 1, root empty,
                    Register 1, class 1,
                    OnNavigate 1 Source,
                    RegisterPending 2 Docs, class 1, root empty,
                    Register 2, class 1,
                    OnNavigate 2 Docs,
                    OnNavigate 1 Source\Deep,
                    Revoke 2
                  ]
                }
                """);
    }

    // Another launch's folders don't open in a window in the tray, so an app looking for one
    // doesn't find it there either.
    [Test]
    public async Task AWindowInTheTrayLeavesTheList()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, fixture.At("Source"));
        list.Name(form, "window");

        form.Hide();
        await Assert.That(list.Describe()).IsEmpty();
        form.Show();

        await Verify(list.Describe())
            .Snapshot(
                """
                [
                  window: Source
                ]
                """);
    }

    [Test]
    public async Task AMovedTabIsInTheListUnderItsNewWindow()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root);
        list.Name(source, "source");
        list.Name(target, "target");

        source.MoveTab((TabPage)source.Views.Last().Parent!, target);

        await Verify(list.Describe())
            .Snapshot(
                """
                [
                  source: Source,
                  target: .,
                  target: Docs
                ]
                """);
    }

    // An app asking for a file shown selected while Explorer opens folders gets Explorer.
    [Test]
    public async Task NoneWhileExplorerOpensFolders()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list, replacingExplorer: false);
        using var form = await Show(fixture, fixture.At("Source"));

        await Assert.That(list.Calls).IsEmpty();
    }

    // What SHOpenFolderAndSelectItems does with the window it finds, step by step. .NET gives
    // IDispatch, which the list takes, only for a public type.
    [Test]
    public async Task TheShellReachesTheViewThroughWhatItFinds()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, fixture.At("Source"));
        var found = list.WindowShowing(fixture.At("Source"));

        Marshal.Release(Marshal.GetIDispatchForObject(found));
        var browser = (ShellWindows.IWebBrowserApp)found;
        browser.get_HWND(out var handle);
        browser.get_Document(out var documentPointer);
        var document = Marshal.GetObjectForIUnknown(documentPointer);
        Marshal.Release(documentPointer);
        var folderView = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
        var shellViewId = new Guid("000214E3-0000-0000-C000-000000000046");
        var served = ((ShellWindows.IComServiceProvider)document).QueryService(ref folderView, ref shellViewId, out var shellView);
        Marshal.Release(shellView);

        await Assert.That(handle).IsEqualTo(form.Handle);
        await Assert.That(served).IsEqualTo(0);
        await Assert.That(shellView).IsNotEqualTo(IntPtr.Zero);
    }

    // As GitHub Desktop's Show in Explorer, on a file in a folder a tab in the background shows.
    [Test]
    public async Task SelectingAnItemPicksItsTab()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, fixture.At("Source"), fixture.Root);

        Select(list, fixture.At("Source", "main.aleph"), selectFirst);
        await Pump.Until(() => form.ActiveView!.SelectedEntries().Count > 0);

        await Verify(
            new
            {
                active = Path.GetFileName(form.ActiveView!.CurrentPath),
                selected = form.ActiveView.SelectedEntries().Select(_ => _.Name).ToList()
            })
            .Snapshot(
                """
                {
                  active: Source,
                  selected: [
                    main.aleph
                  ]
                }
                """);
    }

    [Test]
    public async Task SelectingSeveralItems()
    {
        using var fixture = Fixture.Create();
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, fixture.Root);
        var view = form.ActiveView!;

        Select(list, fixture.At("notes.aleph"), selectFirst);
        Select(list, fixture.At("report2.beta"), selectAnother);
        await Pump.Until(() => view.SelectedEntries().Count == 2);

        await Verify(view.SelectedEntries().Select(_ => _.Name).ToList())
            .Snapshot(
                """
                [
                  notes.aleph,
                  report2.beta
                ]
                """);
    }

    // What a script finding the folder a window shows, through Shell.Application's Windows(),
    // asks of it, by name, through the IDispatch the list was given, as Explorer's list hands it
    // on: where it is, as Explorer gives it, and its document's Folder, the shell's own object
    // for the folder. Path is, as Explorer's, the program's folder, not the window's. A folder
    // with a space and a # in its name is escaped in its url as Explorer escapes it. A share
    // isn't in the list, as naming it to the shell waits on the network, so is asked of a window
    // made for it; its Folder, which would ask the network, isn't asked. The type information
    // is the type library's, which names them as it names Explorer's.
    [Test]
    public async Task AScriptFindsTheFolderAWindowShows()
    {
        using var fixture = Fixture.Create();
        var odd = fixture.At("My Folder #1");
        Directory.CreateDirectory(odd);
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, fixture.Root, odd, Locations.ThisPC);
        var share = new ShellWindows.ShellWindow(null!, IntPtr.Zero, null!, 0, @"\\server\share\My Folder #1", []);
        var shareDispatch = TypedDispatch.DispatchOf(share);
        // Whatever the temp folder's path, a space in the user's name say, escaped.
        var rootUrl = ShellWindows.LocationUrl(fixture.Root);
        var program = Path.GetDirectoryName(Environment.ProcessPath)! + Path.DirectorySeparatorChar;
        string Described(IntPtr window, bool folder = true)
        {
            var lines = new List<string>
            {
                $"LocationURL: {LateBound.Text(window, "LocationURL")}",
                $"LocationName: {LateBound.Text(window, "LocationName")}",
                $"Path: {LateBound.Text(window, "Path")}"
            };
            if (folder)
            {
                using var document = LateBound.Object(window, "Document");
                using var shellFolder = LateBound.Object(document.Pointer, "Folder");
                using var self = LateBound.Object(shellFolder.Pointer, "Self");
                lines.Add($"Document.Folder.Self.Path: {LateBound.Text(self.Pointer, "Path")}");
                lines.Add($"Document.Folder.Title: {LateBound.Text(shellFolder.Pointer, "Title")}");
            }

            return string.Join(" | ", lines)
                .Replace(rootUrl, "{root url}")
                .Replace(fixture.Root, "{root}")
                .Replace(program, "{program}");
        }

        string typeNames;
        using (var document = LateBound.Object(list.DispatchShowing(fixture.Root), "Document"))
        {
            typeNames = $"{LateBound.TypeName(list.DispatchShowing(fixture.Root))}, {LateBound.TypeName(document.Pointer)}";
        }

        await Verify(
            new
            {
                root = Described(list.DispatchShowing(fixture.Root)),
                spaceAndHash = Described(list.DispatchShowing(odd)),
                thisPC = Described(list.DispatchShowing(Locations.ThisPCParsingName)),
                share = Described(shareDispatch, folder: false),
                typeNames
            })
            .Snapshot(
                """
                {
                  root: LocationURL: {root url} | LocationName: Projects | Path: {program} | Document.Folder.Self.Path: {root} | Document.Folder.Title: Projects,
                  spaceAndHash: LocationURL: {root url}/My%20Folder%20%231 | LocationName: My Folder #1 | Path: {program} | Document.Folder.Self.Path: {root}\My Folder #1 | Document.Folder.Title: My Folder #1,
                  thisPC: LocationURL:  | LocationName: This PC | Path: {program} | Document.Folder.Self.Path: ::{20D04FE0-3AEA-1069-A2D8-08002B30309D} | Document.Folder.Title: This PC,
                  share: LocationURL: file://server/share/My%20Folder%20%231 | LocationName: My Folder #1 | Path: {program},
                  typeNames: IWebBrowserApp, IShellFolderViewDual
                }
                """);
        Marshal.Release(shareDispatch);
    }

    // As a script in PowerShell has it, in another process, from what the list hands it: the
    // properties by name, with the type information PowerShell reads them by, which without it
    // it saw none of.
    [Test]
    public async Task PowerShellFindsTheFolderAWindowShows()
    {
        using var fixture = Fixture.Create();
        var odd = fixture.At("My Folder #1");
        Directory.CreateDirectory(odd);
        var list = new FakeList(fixture);
        using var replaced = Replace(list);
        using var form = await Show(fixture, odd);

        // In the running object table, under a class of this run's own, for PowerShell to find
        // it, as it would find a window in Shell.Application's Windows().
        var classId = Guid.NewGuid();
        Marshal.ThrowExceptionForHR(RegisterActiveObject(list.DispatchShowing(odd), ref classId, 0, out var registration));
        string output;
        try
        {
            var script =
                $$"""
                  $ProgressPreference = 'SilentlyContinue'
                  Add-Type -TypeDefinition @'
                  using System; using System.Runtime.InteropServices;
                  public static class RunningObjects {
                      [DllImport("oleaut32.dll", PreserveSig = false)]
                      [return: MarshalAs(UnmanagedType.IUnknown)]
                      public static extern object GetActiveObject(ref Guid classId, IntPtr reserved);
                  }
                  '@
                  $classId = [Guid]'{{classId}}'
                  $window = [RunningObjects]::GetActiveObject([ref]$classId, [IntPtr]::Zero)
                  "LocationURL: $($window.LocationURL)"
                  "LocationName: $($window.LocationName)"
                  "Document.Folder.Self.Path: $($window.Document.Folder.Self.Path)"
                  "Listed: $(($window | Get-Member -MemberType Property | Where-Object Name -in 'LocationURL', 'LocationName').Count)"
                  """;
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(PowerShell.Encode(script));
            using var process = Process.Start(startInfo)!;
            var reading = process.StandardOutput.ReadToEndAsync();
            // What went wrong, should anything, shown with what it wrote.
            var errors = process.StandardError.ReadToEndAsync();

            // Its calls arrive as messages, for this thread to dispatch while it waits.
            await Pump.Until(() => process.HasExited);
            output = (await reading + await errors).Trim();
        }
        finally
        {
            RevokeActiveObject(registration, IntPtr.Zero);
        }

        // Whatever the temp folder's path, a space in the user's name say, escaped.
        var rootUrl = ShellWindows.LocationUrl(fixture.Root);
        await Verify(output.Replace(rootUrl, "{root url}").Replace(fixture.Root, "{root}"))
            .Snapshot(
                """
                LocationURL: {root url}/My%20Folder%20%231
                LocationName: My Folder #1
                Document.Folder.Self.Path: {root}\My Folder #1
                Listed: 2
                """);
    }

    [DllImport("oleaut32.dll")]
    static extern int RegisterActiveObject(IntPtr unknown, ref Guid classId, int flags, out int registration);

    [DllImport("oleaut32.dll")]
    static extern int RevokeActiveObject(int registration, IntPtr reserved);

    /// <summary>
    /// IDispatch called as a script calls it: a name looked up, then its value got, through the
    /// interface's own methods, as another process's calls arrive, rather than through .NET,
    /// which, handed one of Lode's own objects, would call it as .NET.
    /// </summary>
    static class LateBound
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetTypeInfoMethod(IntPtr self, int index, int locale, out IntPtr typeInfo);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetIDsOfNamesMethod(IntPtr self, ref Guid reserved, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names, int count, int locale, [Out] int[] ids);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int InvokeMethod(IntPtr self, int id, ref Guid reserved, int locale, ushort flags, ref Parameters parameters, IntPtr result, IntPtr exception, IntPtr argumentError);

        // DISPPARAMS, with no arguments.
        [StructLayout(LayoutKind.Sequential)]
        struct Parameters
        {
            public IntPtr Arguments;
            public IntPtr NamedArguments;
            public int Count;
            public int NamedCount;
        }

        const ushort propertyGet = 2;
        const short textType = 8;
        const short dispatchType = 9;

        // IDispatch's methods, after IUnknown's three.
        static T Method<T>(IntPtr dispatch, int slot) =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(dispatch), slot * IntPtr.Size));

        public static string Text(IntPtr dispatch, string name) =>
            Get(dispatch, name, textType, _ => Marshal.PtrToStringBSTR(_));

        public static Counted Object(IntPtr dispatch, string name) =>
            Get(
                dispatch,
                name,
                dispatchType,
                _ =>
                {
                    Marshal.AddRef(_);
                    return new Counted(_);
                });

        static T Get<T>(IntPtr dispatch, string name, short expected, Func<IntPtr, T> read)
        {
            var reserved = Guid.Empty;
            var ids = new int[1];
            Marshal.ThrowExceptionForHR(Method<GetIDsOfNamesMethod>(dispatch, 5)(dispatch, ref reserved, [name], 1, 0, ids));
            var parameters = new Parameters();
            // A VARIANT, cleared.
            var result = Marshal.AllocCoTaskMem(32);
            try
            {
                for (var offset = 0; offset < 32; offset += 8)
                {
                    Marshal.WriteInt64(result, offset, 0);
                }

                Marshal.ThrowExceptionForHR(Method<InvokeMethod>(dispatch, 6)(dispatch, ids[0], ref reserved, 0, propertyGet, ref parameters, result, IntPtr.Zero, IntPtr.Zero));
                var type = Marshal.ReadInt16(result);
                if (type != expected)
                {
                    throw new InvalidOperationException($"{name} is of type {type}, not {expected}");
                }

                return read(Marshal.ReadIntPtr(result, 8));
            }
            finally
            {
                VariantClear(result);
                Marshal.FreeCoTaskMem(result);
            }
        }

        public static string TypeName(IntPtr dispatch)
        {
            Marshal.ThrowExceptionForHR(Method<GetTypeInfoMethod>(dispatch, 4)(dispatch, 0, 0, out var pointer));
            try
            {
                var typeInfo = (System.Runtime.InteropServices.ComTypes.ITypeInfo)Marshal.GetObjectForIUnknown(pointer);
                typeInfo.GetDocumentation(-1, out var typeName, out _, out _, out _);
                return typeName;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        [DllImport("oleaut32.dll")]
        static extern int VariantClear(IntPtr variant);
    }

    // An object a script was handed, released once it is done with it.
    sealed class Counted(IntPtr pointer) : IDisposable
    {
        public IntPtr Pointer { get; } = pointer;

        public void Dispose() =>
            Marshal.Release(Pointer);
    }

    static void Select(FakeList list, string path, uint flags)
    {
        var view = (ShellWindows.IShellView)list.WindowShowing(Path.GetDirectoryName(path)!);
        var pidl = Pidl.Parse(path);
        try
        {
            view.SelectItem(ILFindLastID(pidl), flags);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    static Restore Replace(FakeList list, bool replacingExplorer = true)
    {
        var restore = new Restore(ShellWindows.Enabled, ShellWindows.IsReplacingExplorer, ShellWindows.Connect);
        ShellWindows.Enabled = true;
        ShellWindows.IsReplacingExplorer = () => replacingExplorer;
        ShellWindows.Connect = () => list;
        return restore;
    }

    sealed class Restore(bool enabled, Func<bool> isReplacingExplorer, Func<ShellWindows.IShellWindows?> connect) :
        IDisposable
    {
        public void Dispose()
        {
            ShellWindows.Enabled = enabled;
            ShellWindows.IsReplacingExplorer = isReplacingExplorer;
            ShellWindows.Connect = connect;
        }
    }

    static async Task<MainForm> Show(Fixture fixture, params string[] paths)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm(paths, new(), places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        foreach (var view in form.Views)
        {
            await Pump.Loaded(view);
        }

        return form;
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>
    /// Keeps registrations as Explorer's list does: a pending one, which Register completes, and
    /// each found by its folder.
    /// </summary>
    sealed class FakeList(Fixture fixture) : ShellWindows.IShellWindows
    {
        sealed class Entry
        {
            public required string Folder { get; set; }
            public int Window { get; set; }
            public object? Registered { get; set; }

            // What the list was given, as Explorer's hands it to a script.
            public IntPtr Dispatch { get; set; }
        }

        SortedDictionary<int, Entry> entries = [];
        Dictionary<int, string> windowNames = [];
        int lastCookie;
        int pending;

        public List<string> Calls { get; } = [];

        public void Name(MainForm form, string name) =>
            windowNames[unchecked((int)form.Handle.ToInt64())] = name;

        public object WindowShowing(string folder) =>
            EntryShowing(folder).Registered!;

        /// <summary>
        /// The IDispatch the list was given for the window showing the folder, as Explorer's list
        /// hands it to a script asking Shell.Application for its windows.
        /// </summary>
        public IntPtr DispatchShowing(string folder) =>
            EntryShowing(folder).Dispatch;

        Entry EntryShowing(string folder) =>
            entries.Values.First(_ => string.Equals(_.Folder, folder, StringComparison.OrdinalIgnoreCase));

        public List<string> Describe() =>
            entries.Values
                .Select(_ => $"{WindowName(_.Window)}: {FolderName(_.Folder)}")
                .ToList();

        string WindowName(int window) =>
            windowNames.GetValueOrDefault(window, "unnamed");

        string FolderName(string folder) =>
            Path.GetRelativePath(fixture.Root, folder);

        public int RegisterPending(int threadId, ref object location, ref object? root, int windowClass, out int cookie)
        {
            cookie = ++lastCookie;
            pending = cookie;
            var folder = Pidl.PathOf((byte[])location);
            entries[cookie] = new()
            {
                Folder = folder
            };
            Calls.Add($"RegisterPending {cookie} {FolderName(folder)}, class {windowClass}, root {root ?? "empty"}");
            return 0;
        }

        public int Register(IntPtr window, int handle, int windowClass, out int cookie)
        {
            cookie = pending;
            var entry = entries[cookie];
            entry.Window = handle;
            entry.Registered = Marshal.GetObjectForIUnknown(window);
            Marshal.AddRef(window);
            entry.Dispatch = window;
            Calls.Add($"Register {cookie}, class {windowClass}");
            return 0;
        }

        public int OnNavigate(int cookie, ref object location)
        {
            var folder = Pidl.PathOf((byte[])location);
            entries[cookie].Folder = folder;
            Calls.Add($"OnNavigate {cookie} {FolderName(folder)}");
            return 0;
        }

        public int Revoke(int cookie)
        {
            if (entries.Remove(cookie, out var entry) &&
                entry.Dispatch != IntPtr.Zero)
            {
                Marshal.Release(entry.Dispatch);
            }

            Calls.Add($"Revoke {cookie}");
            return 0;
        }

        public int get_Count(out int count)
        {
            count = entries.Count;
            return 0;
        }

        public int Item(object index, out object? folder) => throw new NotImplementedException();
        public int _NewEnum(out object? enumerator) => throw new NotImplementedException();
        public int OnActivated(int cookie, short active) => throw new NotImplementedException();
        public int FindWindowSW(ref object location, ref object? root, int windowClass, out int handle, int options, out object? window) => throw new NotImplementedException();
        public int OnCreated(int cookie, object created) => throw new NotImplementedException();
        public int ProcessAttachDetach(short attach) => throw new NotImplementedException();
    }

    static class Pidl
    {
        public static IntPtr Parse(string path)
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
            return pidl;
        }

        public static string PathOf(byte[] pidl)
        {
            var pointer = Marshal.AllocCoTaskMem(pidl.Length);
            try
            {
                Marshal.Copy(pidl, 0, pointer, pidl.Length);
                Marshal.ThrowExceptionForHR(SHGetNameFromIDList(pointer, 0x80028000, out var name));
                try
                {
                    return Marshal.PtrToStringUni(name)!;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern int SHGetNameFromIDList(IntPtr pidl, uint form, out IntPtr name);
}
