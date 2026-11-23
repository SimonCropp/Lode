/// <summary>
/// Tabs in the shell's list of folder windows, where an app asking for a file shown selected (GitHub
/// Desktop's Show in Explorer, say) finds the one showing its folder. Against a list of the
/// tests' own, rather than Explorer's.
/// </summary>
[STAThreadExecutor]
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
        browser.get_Document(out var document);
        var folderView = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
        var shellViewId = new Guid("000214E3-0000-0000-C000-000000000046");
        var served = ((ShellWindows.IComServiceProvider)document!).QueryService(ref folderView, ref shellViewId, out var shellView);
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
        }

        SortedDictionary<int, Entry> entries = [];
        Dictionary<int, string> windowNames = [];
        int lastCookie;
        int pending;

        public List<string> Calls { get; } = [];

        public void Name(MainForm form, string name) =>
            windowNames[unchecked((int)form.Handle.ToInt64())] = name;

        public object WindowShowing(string folder) =>
            entries.Values.First(_ => string.Equals(_.Folder, folder, StringComparison.OrdinalIgnoreCase)).Registered!;

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

        public int Register(object window, int handle, int windowClass, out int cookie)
        {
            cookie = pending;
            var entry = entries[cookie];
            entry.Window = handle;
            entry.Registered = window;
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
            entries.Remove(cookie);
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
