/// <summary>
/// Puts each tab, in a window that is showing, in the shell's list of folder windows, as File
/// Explorer's windows are, while Lode opens folders in place of Explorer. An app asking the shell
/// to show a file selected (SHOpenFolderAndSelectItems: GitHub Desktop's Show in Explorer on a
/// file, a browser's Show in folder, VS Code's Reveal in File Explorer) looks in that list for a
/// window showing the file's folder, and asks it to select the file. Finding none, it opens the
/// folder, which starts Lode, then waits for a window showing the folder to join the list. With no
/// Lode tab ever there, it gave up after 20 seconds, so the folder opened with nothing selected.
/// </summary>
/// <remarks>
/// What the shell asks of a window it finds, as Explorer++ found: the object registered for it,
/// as <see cref="IWebBrowserApp"/>, for its window handle, to bring that to the front, and for its
/// document; the document, as <see cref="IComServiceProvider"/>, for the IFolderView service as
/// <see cref="IShellView"/>; and that, to select each item. <see cref="ShellWindow"/> is the first
/// and the last, <see cref="ShellFolderView"/> the document. A script finding the folders Explorer
/// shows, through Shell.Application's Windows(), finds Lode's tabs there too, and asks the same of
/// them: LocationURL, LocationName, and Document.Folder.Self.Path.
/// Every tab of a window is registered under the window's handle, each with its own cookie, so
/// a folder in a tab in the background is found too, and selecting in it picks the tab. A tab in
/// a window in the tray is taken out, since another launch would not open in it either (see
/// <see cref="WindowContext"/>). Somewhere on the network is left out: naming it to the shell
/// can wait on the network, and this runs on the UI thread.
/// </remarks>
public static partial class ShellWindows
{
    /// <summary>
    /// Whether tabs join the list at all. The running app turns it on. Tests make windows that
    /// must stay out of the real list, unless a test puts a list of its own in <see cref="Connect"/>.
    /// </summary>
    internal static bool Enabled { get; set; }

    /// <summary>
    /// Whether Lode is what opens folders, which is when an app would expect the shell to find
    /// its windows. Asked at each change, since another launch (lode --replace-explorer) can
    /// change it.
    /// </summary>
    internal static Func<bool> IsReplacingExplorer = () => ExplorerReplacement.ForCurrentUser().IsActive;

    /// <summary>
    /// Gets the shell's list, in Explorer's process, or null when it can't be had. Replaced in
    /// tests, which connects to the replacement from the next change on.
    /// </summary>
    internal static Func<IShellWindows?> Connect
    {
        get;
        set
        {
            field = value;
            list = null;
        }
    } = ConnectToShell;

    // Explorer's own windows register as this, "an Internet Explorer window", and it is the only
    // kind SHOpenFolderAndSelectItems looks for.
    const int browserWindow = 1;

    const int notImplemented = unchecked((int)0x80004001);
    const int noInterface = unchecked((int)0x80004002);
    const int failed = unchecked((int)0x80004005);

    // SVSIF: SelectItem's flags.
    const uint selectFlag = 0x1;
    const uint deselectOthersFlag = 0x4;

    // IFolderView, which is also the service the shell asks a document for to reach its view.
    static Guid folderViewService = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");

    static Guid shellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    static IShellWindows? list;
    static Dictionary<FolderView, ShellWindow> registered = [];

    // A change while the list is being brought in line, from a call the shell made into Lode
    // while Lode was calling out to it, waits for the one under way to finish.
    static bool syncing;
    static List<MainForm> waiting = [];

    /// <summary>
    /// Brings the list in line with each window open, as after Replace or Restore.
    /// </summary>
    internal static void SyncAll()
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            Sync(window);
        }
    }

    /// <summary>
    /// Brings the list in line with the window's tabs: each in it, under the folder it shows,
    /// while the window shows and Lode opens folders, and out of it otherwise.
    /// </summary>
    internal static void Sync(MainForm window)
    {
        if (syncing)
        {
            if (!waiting.Contains(window))
            {
                waiting.Add(window);
            }

            return;
        }

        syncing = true;
        try
        {
            SyncNow(window);
            while (waiting.Count > 0)
            {
                var next = waiting[0];
                waiting.RemoveAt(0);
                SyncNow(next);
            }
        }
        finally
        {
            syncing = false;
        }
    }

    /// <summary>
    /// Explorer started again, after a crash or from Task Manager, with a list of its own that
    /// has none of Lode's tabs. Each window is told, and puts its own tabs in the new list.
    /// </summary>
    internal static void ExplorerRestarted(MainForm window)
    {
        // The first window told makes a new list; the rest find that one working.
        if (list != null &&
            !Answers(list))
        {
            list = null;
        }

        Sync(window);
    }

    static void SyncNow(MainForm window)
    {
        if (window.IsDisposed)
        {
            return;
        }

        var shown = Enabled &&
                    window.Visible &&
                    IsReplacingExplorer();
        foreach (var view in window.Views.ToList())
        {
            if (shown)
            {
                Show(view, window.Handle);
            }
            else
            {
                Revoke(view);
            }
        }
    }

    static void Show(FolderView view, IntPtr window)
    {
        // Not one moved to another window, or registered with a list Explorer has since replaced.
        var existing = registered.GetValueOrDefault(view);
        if (existing != null &&
            (existing.Window != window || existing.List != list))
        {
            Revoke(view);
            existing = null;
        }

        if (existing?.Path == view.CurrentPath)
        {
            return;
        }

        var folder = Identify(view.CurrentPath);
        if (folder == null)
        {
            // Gone somewhere left out of the list.
            Revoke(view);
            return;
        }

        if (existing == null)
        {
            Register(view, window, folder);
            return;
        }

        existing.Navigated(view.CurrentPath, folder);
        object location = folder;
        if (existing.List.OnNavigate(existing.Cookie, ref location) >= 0)
        {
            return;
        }

        // In a new list, if Explorer has one.
        Disconnected(existing.List);
        Revoke(view);
        Register(view, window, folder);
    }

    static void Register(FolderView view, IntPtr window, byte[] folder)
    {
        list ??= Connect();
        if (list == null)
        {
            return;
        }

        var target = list;

        // The pidl again in RegisterPending, though OnNavigate gives it, as Explorer++ found that
        // a window that joins the list while SHOpenFolderAndSelectItems waits is otherwise never
        // asked to select anything. Register then finds the pending one by the window's thread,
        // so the two go together, with nothing registered in between.
        object? root = null;
        object location = folder;
        if (target.RegisterPending(GetCurrentThreadId(), ref location, ref root, browserWindow, out var cookie) < 0)
        {
            Disconnected(target);
            return;
        }

        var shellWindow = new ShellWindow(view, window, target, cookie, view.CurrentPath, folder);
        var done = false;
        var dispatch = IntPtr.Zero;
        try
        {
            // The IDispatch a script calls by name: see TypedDispatch. The handle as a long, as
            // the list keeps it: only its lower 32 bits mean anything.
            dispatch = TypedDispatch.DispatchOf(shellWindow);
            done = target.Register(dispatch, unchecked((int)window.ToInt64()), browserWindow, out _) >= 0;
        }
        finally
        {
            if (dispatch != IntPtr.Zero)
            {
                Marshal.Release(dispatch);
            }

            // A pending window left in the list is one SHOpenFolderAndSelectItems waits the full
            // 20 seconds for, for that folder, every time, until Explorer restarts.
            if (!done)
            {
                target.Revoke(cookie);
            }
        }

        if (!done)
        {
            Disconnected(target);
            return;
        }

        location = folder;
        target.OnNavigate(cookie, ref location);
        registered[view] = shellWindow;
        view.Disposed += OnViewDisposed;
    }

    static void OnViewDisposed(object? sender, EventArgs args) =>
        Revoke((FolderView)sender!);

    static void Revoke(FolderView view)
    {
        if (!registered.Remove(view, out var shellWindow))
        {
            return;
        }

        view.Disposed -= OnViewDisposed;
        shellWindow.Detach();
        // A list Explorer has replaced went with it, and its cookies with it.
        shellWindow.List.Revoke(shellWindow.Cookie);
    }

    // A call to the list failed: Explorer has gone, and the next change makes a new one.
    static void Disconnected(IShellWindows failing)
    {
        if (list == failing)
        {
            list = null;
        }
    }

    static bool Answers(IShellWindows target)
    {
        try
        {
            return target.get_Count(out _) >= 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    static IShellWindows? ConnectToShell()
    {
        var type = Type.GetTypeFromCLSID(shellWindowsClass);
        if (type == null)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(type) as IShellWindows;
        }
        catch (COMException)
        {
            // No Explorer running to hold the list.
            return null;
        }
    }

    /// <summary>
    /// The folder as the shell identifies it, a pidl, or null for one left out of the list.
    /// </summary>
    static byte[]? Identify(string path)
    {
        string parsingName;
        if (Locations.IsThisPC(path))
        {
            parsingName = Locations.ThisPCParsingName;
        }
        else if (Locations.IsRemote(path) ||
                 Zip.IsArchivePath(path))
        {
            // Inside a zip, the shell's own zip folder would be what the tab is registered as.
            return null;
        }
        else
        {
            parsingName = path;
        }

        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _) < 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[ILGetSize(pidl)];
            Marshal.Copy(pidl, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// The path of an item in the folder, from the pidl the shell names it by, relative to the
    /// folder. Null when the shell can't name it.
    /// </summary>
    internal static string? ChildPath(byte[] folder, IntPtr child)
    {
        var parent = Marshal.AllocCoTaskMem(folder.Length);
        try
        {
            Marshal.Copy(folder, 0, parent, folder.Length);
            var full = ILCombine(parent, child);
            if (full == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (SHGetNameFromIDList(full, desktopAbsoluteParsing, out var name) < 0)
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
        finally
        {
            Marshal.FreeCoTaskMem(parent);
        }
    }

    /// <summary>
    /// A tab in the list: what the shell finds for it, its view and its window all at once, and
    /// what a script finds for it in Shell.Application's Windows(), as it would one of Explorer's:
    /// where it is, LocationURL and LocationName, and its <see cref="ShellFolderView"/> document,
    /// whose Folder is the folder itself. A script calls these by name, through the IDispatch
    /// <see cref="TypedDispatch"/> gives it, as Explorer's own type library names them.
    /// </summary>
    /// <remarks>
    /// Public, as is <see cref="IWebBrowserApp"/>, only because .NET gives an object IDispatch,
    /// which the list takes, only for a public type. Nothing else about it is.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IWebBrowserApp))]
    public sealed class ShellWindow :
        IWebBrowserApp,
        IComServiceProvider,
        IShellView,
        ICustomQueryInterface
    {
        // Null once out of the list: the app that found it may still hold it.
        FolderView? view;
        SynchronizationContext? context = SynchronizationContext.Current;

        // Made the first time each is asked for, and kept, as an app holds on to what it found.
        ShellFolderView? document;
        TypedDispatch? dispatch;
        bool dispatchMade;

        // What an app has asked to have selected, since it asked for the others to be
        // deselected, and whether showing them is on its way.
        List<string> selecting = [];
        bool showing;

        internal ShellWindow(FolderView view, IntPtr window, IShellWindows list, int cookie, string path, byte[] folder)
        {
            this.view = view;
            Window = window;
            List = list;
            Cookie = cookie;
            Path = path;
            Folder = folder;
        }

        internal IntPtr Window { get; }
        internal IShellWindows List { get; }
        internal int Cookie { get; }
        internal string Path { get; private set; }
        internal byte[] Folder { get; private set; }

        internal void Navigated(string newPath, byte[] newFolder)
        {
            Path = newPath;
            Folder = newFolder;
        }

        internal void Detach() =>
            view = null;

        int IShellView.SelectItem(IntPtr child, uint flags)
        {
            if (view == null)
            {
                return failed;
            }

            if ((flags & selectFlag) == 0)
            {
                return notImplemented;
            }

            if (ChildPath(Folder, child) is not { } item)
            {
                return failed;
            }

            if ((flags & deselectOthersFlag) != 0)
            {
                selecting = [];
            }

            selecting.Add(item);

            // Once the shell is done asking, rather than in the middle of its call, which can
            // arrive while Lode is waiting on a call of its own.
            if (!showing)
            {
                showing = true;
                Post(
                    () =>
                    {
                        showing = false;
                        if (view != null)
                        {
                            MainForm.ShowItems(view, selecting.ToList());
                        }
                    });
            }

            return 0;
        }

        void Post(Action action)
        {
            if (context == null)
            {
                action();
                return;
            }

            context.Post(_ => action(), null);
        }

        int IComServiceProvider.QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (service != folderViewService)
            {
                return noInterface;
            }

            var unknown = Marshal.GetIUnknownForObject(this);
            try
            {
                return Marshal.QueryInterface(unknown, interfaceId, out result);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid interfaceId, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (!TypedDispatch.IsDispatch(interfaceId))
            {
                return CustomQueryInterfaceResult.NotHandled;
            }

            if (!dispatchMade)
            {
                dispatchMade = true;
                dispatch = TypedDispatch.Create(this, typeof(IWebBrowserApp), internetControlsLibrary, 1, 1);
            }

            return dispatch?.QueryInterface(out result) ?? CustomQueryInterfaceResult.NotHandled;
        }

        int IWebBrowserApp.get_Document(out IntPtr document)
        {
            document = TypedDispatch.DispatchOf(this.document ??= new(this));
            return 0;
        }

        int IWebBrowserApp.get_HWND(out IntPtr handle)
        {
            handle = Window;
            return 0;
        }

        // As Explorer gives it, file:///C:/My%20Folder, or file://server/share for a share, and
        // nothing for This PC, which is no file.
        int IWebBrowserApp.get_LocationURL(out IntPtr url)
        {
            url = Marshal.StringToBSTR(LocationUrl(Path));
            return 0;
        }

        // As Explorer shows it, and names its window: Downloads, Local Disk (C:), This PC.
        int IWebBrowserApp.get_LocationName(out IntPtr name)
        {
            name = Marshal.StringToBSTR(LocationName(Path, Folder));
            return 0;
        }

        // Not the folder, which Explorer gives as LocationURL, and as its document's Folder, but,
        // as Explorer's is C:\Windows\, the folder of the program the window is.
        int IWebBrowserApp.get_Path(out IntPtr folder)
        {
            folder = Marshal.StringToBSTR(ProgramFolder());
            return 0;
        }

        /// <summary>
        /// The shell's own Folder object for the folder the tab shows, as Shell.Application's
        /// NameSpace gives a script, counted: the same kind of object Explorer's document gives as
        /// its Folder, with its Self, its Title, its Items and the rest. Zero where the shell has
        /// none for it.
        /// </summary>
        internal IntPtr ShellFolder()
        {
            var parsingName = Locations.IsThisPC(Path) ? Locations.ThisPCParsingName : Path;
            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromCLSID(shellApplication)!);
                if (shell is not IShellDispatch application ||
                    application.NameSpace(parsingName, out var folder) < 0)
                {
                    return IntPtr.Zero;
                }

                return folder;
            }
            catch (COMException)
            {
                return IntPtr.Zero;
            }
            finally
            {
                if (shell != null)
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
        }

        // The rest of a browser window's and a shell view's, which neither the shell nor a script
        // finding a window's folder asks for.
        int IWebBrowserApp.GoBack() => notImplemented;
        int IWebBrowserApp.GoForward() => notImplemented;
        int IWebBrowserApp.GoHome() => notImplemented;
        int IWebBrowserApp.GoSearch() => notImplemented;
        int IWebBrowserApp.Navigate(IntPtr url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers) => notImplemented;
        int IWebBrowserApp.Refresh() => notImplemented;
        int IWebBrowserApp.Refresh2(IntPtr level) => notImplemented;
        int IWebBrowserApp.Stop() => notImplemented;
        int IWebBrowserApp.get_Application(out IntPtr application) => NotImplemented(out application);
        int IWebBrowserApp.get_Parent(out IntPtr parent) => NotImplemented(out parent);
        int IWebBrowserApp.get_Container(out IntPtr container) => NotImplemented(out container);
        int IWebBrowserApp.get_TopLevelContainer(out short topLevel) => NotImplemented(out topLevel);
        int IWebBrowserApp.get_Type(out IntPtr type) => NotImplemented(out type);
        int IWebBrowserApp.get_Left(out int left) => NotImplemented(out left);
        int IWebBrowserApp.put_Left(int left) => notImplemented;
        int IWebBrowserApp.get_Top(out int top) => NotImplemented(out top);
        int IWebBrowserApp.put_Top(int top) => notImplemented;
        int IWebBrowserApp.get_Width(out int width) => NotImplemented(out width);
        int IWebBrowserApp.put_Width(int width) => notImplemented;
        int IWebBrowserApp.get_Height(out int height) => NotImplemented(out height);
        int IWebBrowserApp.put_Height(int height) => notImplemented;
        int IWebBrowserApp.get_Busy(out short busy) => NotImplemented(out busy);
        int IWebBrowserApp.Quit() => notImplemented;
        int IWebBrowserApp.ClientToWindow(ref int width, ref int height) => notImplemented;
        int IWebBrowserApp.PutProperty(string property, object value) => notImplemented;
        int IWebBrowserApp.GetProperty(string property, out object? value) => NotImplemented(out value);
        int IWebBrowserApp.get_Name(out IntPtr name) => NotImplemented(out name);
        int IWebBrowserApp.get_FullName(out IntPtr fullName) => NotImplemented(out fullName);
        int IWebBrowserApp.get_Visible(out short visible) => NotImplemented(out visible);
        int IWebBrowserApp.put_Visible(short visible) => notImplemented;
        int IWebBrowserApp.get_StatusBar(out short statusBar) => NotImplemented(out statusBar);
        int IWebBrowserApp.put_StatusBar(short statusBar) => notImplemented;
        int IWebBrowserApp.get_StatusText(out IntPtr text) => NotImplemented(out text);
        int IWebBrowserApp.put_StatusText(IntPtr text) => notImplemented;
        int IWebBrowserApp.get_ToolBar(out int toolBar) => NotImplemented(out toolBar);
        int IWebBrowserApp.put_ToolBar(int toolBar) => notImplemented;
        int IWebBrowserApp.get_MenuBar(out short menuBar) => NotImplemented(out menuBar);
        int IWebBrowserApp.put_MenuBar(short menuBar) => notImplemented;
        int IWebBrowserApp.get_FullScreen(out short fullScreen) => NotImplemented(out fullScreen);
        int IWebBrowserApp.put_FullScreen(short fullScreen) => notImplemented;
        int IShellView.GetWindow(out IntPtr handle) => NotImplemented(out handle);
        int IShellView.ContextSensitiveHelp(int enterMode) => notImplemented;
        int IShellView.TranslateAccelerator(IntPtr message) => notImplemented;
        int IShellView.EnableModeless(int enable) => notImplemented;
        int IShellView.UIActivate(uint state) => notImplemented;
        int IShellView.Refresh() => notImplemented;
        int IShellView.CreateViewWindow(IntPtr previous, IntPtr folderSettings, IntPtr browser, IntPtr bounds, out IntPtr handle) => NotImplemented(out handle);
        int IShellView.DestroyViewWindow() => notImplemented;
        int IShellView.GetCurrentInfo(IntPtr folderSettings) => notImplemented;
        int IShellView.AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr parameter) => notImplemented;
        int IShellView.SaveViewState() => notImplemented;
        int IShellView.GetItemObject(uint item, ref Guid interfaceId, out IntPtr result) => NotImplemented(out result);

        static int NotImplemented<T>(out T? value)
        {
            value = default;
            return notImplemented;
        }
    }

    /// <summary>
    /// A tab's document, as the shell finds it from the tab and a script from its window's
    /// Document: to the shell, the way to the view, to select in it; to a script, the folder, as
    /// Folder, the shell's own object for it, so <c>Document.Folder.Self.Path</c> is the path of
    /// the folder the tab shows, as it is of the folder Explorer's window shows.
    /// </summary>
    /// <remarks>
    /// Public, as <see cref="ShellWindow"/> is, for its IDispatch.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IShellFolderViewDual))]
    public sealed class ShellFolderView :
        IShellFolderViewDual,
        IComServiceProvider,
        ICustomQueryInterface
    {
        static Guid folderViewDualId = typeof(IShellFolderViewDual).GUID;

        readonly ShellWindow window;
        TypedDispatch? dispatch;
        bool dispatchMade;

        internal ShellFolderView(ShellWindow window) =>
            this.window = window;

        int IComServiceProvider.QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result) =>
            ((IComServiceProvider)window).QueryService(ref service, ref interfaceId, out result);

        CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid interfaceId, out IntPtr result)
        {
            result = IntPtr.Zero;

            // A script's, through IDispatch, of which Folder alone does anything. Asked for as
            // itself, it is turned down, as it was before the document had it, so the shell,
            // should it ask, still selects through the view rather than its SelectItem, which
            // does nothing.
            if (interfaceId == folderViewDualId)
            {
                return CustomQueryInterfaceResult.Failed;
            }

            if (!TypedDispatch.IsDispatch(interfaceId))
            {
                return CustomQueryInterfaceResult.NotHandled;
            }

            if (!dispatchMade)
            {
                dispatchMade = true;
                dispatch = TypedDispatch.Create(this, typeof(IShellFolderViewDual), shellLibrary, 1, 0);
            }

            return dispatch?.QueryInterface(out result) ?? CustomQueryInterfaceResult.NotHandled;
        }

        int IShellFolderViewDual.get_Folder(out IntPtr folder)
        {
            folder = window.ShellFolder();
            return folder == IntPtr.Zero ? failed : 0;
        }

        // The rest of a view's, which a script finding the folder doesn't ask for.
        int IShellFolderViewDual.get_Application(out IntPtr application) => NotImplemented(out application);
        int IShellFolderViewDual.get_Parent(out IntPtr parent) => NotImplemented(out parent);
        int IShellFolderViewDual.SelectedItems(out IntPtr items) => NotImplemented(out items);
        int IShellFolderViewDual.get_FocusedItem(out IntPtr item) => NotImplemented(out item);
        int IShellFolderViewDual.SelectItem(IntPtr item, int flags) => notImplemented;
        int IShellFolderViewDual.PopupItemMenu(IntPtr item, object x, object y, out IntPtr chosen) => NotImplemented(out chosen);
        int IShellFolderViewDual.get_Script(out IntPtr script) => NotImplemented(out script);
        int IShellFolderViewDual.get_ViewOptions(out int options) => NotImplemented(out options);

        static int NotImplemented<T>(out T? value)
        {
            value = default;
            return notImplemented;
        }
    }

    // The type libraries Explorer's window and its document are described by: Microsoft Internet
    // Controls (SHDocVw), for IWebBrowserApp, and Microsoft Shell Controls And Automation, for
    // IShellFolderViewDual.
    static Guid internetControlsLibrary = new("EAB22AC0-30C1-11CF-A7EB-0000C05BAE0B");
    static Guid shellLibrary = new("50A7E9B0-70EF-11D1-B75A-00A0C90564FE");

    // Shell.Application.
    static Guid shellApplication = new("13709620-C279-11CE-A49E-444553540000");

    /// <summary>
    /// A folder as Explorer gives it as a window's LocationURL: escaped as the shell escapes a
    /// path into a url, file:///C:/My%20Folder%23, a share's file://server/share, and nothing
    /// for This PC, which is no file.
    /// </summary>
    internal static string LocationUrl(string path)
    {
        if (Locations.IsThisPC(path))
        {
            return "";
        }

        // Each character escaped as three at most, and file:/// before them.
        var buffer = new char[path.Length * 3 + 16];
        var length = (uint)buffer.Length;
        if (UrlCreateFromPathW(path, buffer, ref length, 0) < 0)
        {
            return "";
        }

        return new(buffer, 0, (int)length);
    }

    /// <summary>
    /// A folder as Explorer names it, as a window's LocationName: as the shell shows it, for a
    /// folder on this machine, Downloads, Local Disk (C:), This PC, from <paramref name="folder"/>,
    /// its pidl. Somewhere on the network, which asking the shell about would wait on, is named
    /// as Lode names it, which is as Explorer does: a share as share (\\server).
    /// </summary>
    internal static string LocationName(string path, byte[] folder)
    {
        if (Locations.IsRemote(path) ||
            folder.Length == 0)
        {
            return Locations.DisplayName(path);
        }

        var pidl = Marshal.AllocCoTaskMem(folder.Length);
        try
        {
            Marshal.Copy(folder, 0, pidl, folder.Length);
            if (SHGetNameFromIDList(pidl, normalDisplay, out var name) < 0)
            {
                return Locations.DisplayName(path);
            }

            try
            {
                return Marshal.PtrToStringUni(name) ?? Locations.DisplayName(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    // The folder of the program running, with the separator after it, as Explorer gives its own,
    // C:\Windows\.
    static string ProgramFolder() =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) + Path.DirectorySeparatorChar;

    // IShellFolderViewDual, what Explorer's document is to a script, with every method in its
    // place, as the standard IDispatch calls them by their places in the vtable.
    [ComImport]
    [Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IShellFolderViewDual
    {
        [PreserveSig]
        int get_Application(out IntPtr application);

        [PreserveSig]
        int get_Parent(out IntPtr parent);

        [PreserveSig]
        int get_Folder(out IntPtr folder);

        [PreserveSig]
        int SelectedItems(out IntPtr items);

        [PreserveSig]
        int get_FocusedItem(out IntPtr item);

        [PreserveSig]
        int SelectItem(IntPtr item, int flags);

        [PreserveSig]
        int PopupItemMenu(IntPtr item, [MarshalAs(UnmanagedType.Struct)] object x, [MarshalAs(UnmanagedType.Struct)] object y, out IntPtr chosen);

        [PreserveSig]
        int get_Script(out IntPtr script);

        [PreserveSig]
        int get_ViewOptions(out int options);
    }

    // Shell.Application's IShellDispatch, as far as NameSpace, the one method of it Lode calls.
    [ComImport]
    [Guid("D8F015C0-C278-11CE-A49E-444553540000")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShellDispatch
    {
        [PreserveSig]
        int get_Application(out IntPtr application);

        [PreserveSig]
        int get_Parent(out IntPtr parent);

        [PreserveSig]
        int NameSpace([MarshalAs(UnmanagedType.Struct)] object folder, out IntPtr result);
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShellWindows
    {
        [PreserveSig]
        int get_Count(out int count);

        [PreserveSig]
        int Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object? folder);

        [PreserveSig]
        int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? enumerator);

        // The window's IDispatch as Lode asks for it, rather than as .NET's marshalling would.
        [PreserveSig]
        int Register(IntPtr window, int handle, int windowClass, out int cookie);

        [PreserveSig]
        int RegisterPending(int threadId, ref object location, ref object? root, int windowClass, out int cookie);

        [PreserveSig]
        int Revoke(int cookie);

        [PreserveSig]
        int OnNavigate(int cookie, ref object location);

        [PreserveSig]
        int OnActivated(int cookie, short active);

        [PreserveSig]
        int FindWindowSW(ref object location, ref object? root, int windowClass, out int handle, int options, [MarshalAs(UnmanagedType.IDispatch)] out object? window);

        [PreserveSig]
        int OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object created);

        [PreserveSig]
        int ProcessAttachDetach(short attach);
    }

    // IServiceProvider, by another name than .NET's own. Every method of each of these is
    // declared, in order, whether Lode implements it or not: another app reaches them by their
    // place in the interface.
    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellView
    {
        // IOleWindow
        [PreserveSig]
        int GetWindow(out IntPtr handle);

        [PreserveSig]
        int ContextSensitiveHelp(int enterMode);

        [PreserveSig]
        int TranslateAccelerator(IntPtr message);

        [PreserveSig]
        int EnableModeless(int enable);

        [PreserveSig]
        int UIActivate(uint state);

        [PreserveSig]
        int Refresh();

        [PreserveSig]
        int CreateViewWindow(IntPtr previous, IntPtr folderSettings, IntPtr browser, IntPtr bounds, out IntPtr handle);

        [PreserveSig]
        int DestroyViewWindow();

        [PreserveSig]
        int GetCurrentInfo(IntPtr folderSettings);

        [PreserveSig]
        int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr parameter);

        [PreserveSig]
        int SaveViewState();

        [PreserveSig]
        int SelectItem(IntPtr child, uint flags);

        [PreserveSig]
        int GetItemObject(uint item, ref Guid interfaceId, out IntPtr result);
    }

    // IWebBrowserApp, with IWebBrowser's methods before its own, as COM lays them out.
    [ComImport]
    [Guid("0002DF05-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IWebBrowserApp
    {
        [PreserveSig]
        int GoBack();

        [PreserveSig]
        int GoForward();

        [PreserveSig]
        int GoHome();

        [PreserveSig]
        int GoSearch();

        [PreserveSig]
        int Navigate(IntPtr url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers);

        [PreserveSig]
        int Refresh();

        [PreserveSig]
        int Refresh2(IntPtr level);

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int get_Application(out IntPtr application);

        [PreserveSig]
        int get_Parent(out IntPtr parent);

        [PreserveSig]
        int get_Container(out IntPtr container);

        [PreserveSig]
        int get_Document(out IntPtr document);

        [PreserveSig]
        int get_TopLevelContainer(out short topLevel);

        [PreserveSig]
        int get_Type(out IntPtr type);

        [PreserveSig]
        int get_Left(out int left);

        [PreserveSig]
        int put_Left(int left);

        [PreserveSig]
        int get_Top(out int top);

        [PreserveSig]
        int put_Top(int top);

        [PreserveSig]
        int get_Width(out int width);

        [PreserveSig]
        int put_Width(int width);

        [PreserveSig]
        int get_Height(out int height);

        [PreserveSig]
        int put_Height(int height);

        [PreserveSig]
        int get_LocationName(out IntPtr name);

        [PreserveSig]
        int get_LocationURL(out IntPtr url);

        [PreserveSig]
        int get_Busy(out short busy);

        [PreserveSig]
        int Quit();

        [PreserveSig]
        int ClientToWindow(ref int width, ref int height);

        [PreserveSig]
        int PutProperty([MarshalAs(UnmanagedType.BStr)] string property, object value);

        [PreserveSig]
        int GetProperty([MarshalAs(UnmanagedType.BStr)] string property, out object? value);

        [PreserveSig]
        int get_Name(out IntPtr name);

        [PreserveSig]
        int get_HWND(out IntPtr handle);

        [PreserveSig]
        int get_FullName(out IntPtr fullName);

        [PreserveSig]
        int get_Path(out IntPtr folder);

        [PreserveSig]
        int get_Visible(out short visible);

        [PreserveSig]
        int put_Visible(short visible);

        [PreserveSig]
        int get_StatusBar(out short statusBar);

        [PreserveSig]
        int put_StatusBar(short statusBar);

        [PreserveSig]
        int get_StatusText(out IntPtr text);

        [PreserveSig]
        int put_StatusText(IntPtr text);

        [PreserveSig]
        int get_ToolBar(out int toolBar);

        [PreserveSig]
        int put_ToolBar(int toolBar);

        [PreserveSig]
        int get_MenuBar(out short menuBar);

        [PreserveSig]
        int put_MenuBar(short menuBar);

        [PreserveSig]
        int get_FullScreen(out short fullScreen);

        [PreserveSig]
        int put_FullScreen(short fullScreen);
    }

    // SIGDN_DESKTOPABSOLUTEPARSING, and SIGDN_NORMALDISPLAY, what the shell shows.
    const uint desktopAbsoluteParsing = 0x80028000;
    const uint normalDisplay = 0x00000000;

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int UrlCreateFromPathW(string path, [Out] char[] url, ref uint length, uint reserved);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentThreadId();

    [LibraryImport("shell32.dll")]
    private static partial uint ILGetSize(IntPtr identifier);

    [LibraryImport("shell32.dll")]
    private static partial IntPtr ILCombine(IntPtr first, IntPtr second);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetNameFromIDList(IntPtr identifier, uint form, out IntPtr name);
}
