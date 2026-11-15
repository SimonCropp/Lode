/// <summary>
/// The running app: its windows and its tray icon. Closing the last window hides it to the
/// tray rather than exiting; only Exit (in the tray menu or the window menu), or Windows
/// shutting down, ends the app.
/// </summary>
sealed class WindowContext : ApplicationContext
{
    // The window used most recently first, which is where another launch's folders go.
    List<MainForm> windows = [];
    Settings settings;
    Places places;
    NotifyIcon tray;
    Icon trayIcon = ShellIcons.FolderWindowIcon();

    public WindowContext(MainForm first, Settings settings, Places places, SingleInstance instance)
    {
        this.settings = settings;
        this.places = places;

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Lode", null, (_, _) => ShowWindows());
        open.Font = new(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add("New window", null, (_, _) => OpenWindow([settings.StartLocation()]));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new()
        {
            Icon = trayIcon,
            Text = "Lode",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, eventArgs) =>
        {
            switch (TrayClick(eventArgs.Button, Control.ModifierKeys))
            {
                case TrayAction.NewWindow:
                    OpenWindow([settings.StartLocation()]);
                    break;
                case TrayAction.ShowWindows:
                    ShowWindows();
                    break;
            }
        };

        TrayPinning.Pin(Environment.ProcessPath!);

        global::MainForm.HideLastWindowOnClose = true;
        ShellContextMenu.WarmsUpBehindMenus = true;
        global::MainForm.WindowOpened += Track;
        global::MainForm.ExitRequested += Exit;
        Track(first);
        first.Show();
        instance.Listen(OnLaunched);
        ReportUpdate();
        Updates.Instance.Start();
    }

    // Lode goes away while it updates and comes back, which looks the same whether the update
    // worked or not, so the Lode it starts says which.
    void ReportUpdate()
    {
        if (Updates.Instance.Report() is not { } report)
        {
            return;
        }

        tray.ShowBalloonTip(10000, report.Title, report.Message, report.Icon);
    }

    internal enum TrayAction
    {
        None,
        ShowWindows,
        NewWindow
    }

    /// <summary>
    /// What a click on the tray icon does. A left click brings the windows back; Shift+click
    /// opens another, as it does on a taskbar button. Right-click is the icon's menu.
    /// </summary>
    internal static TrayAction TrayClick(MouseButtons button, Keys modifiers)
    {
        if (button != MouseButtons.Left)
        {
            return TrayAction.None;
        }

        if (modifiers.HasFlag(Keys.Shift))
        {
            return TrayAction.NewWindow;
        }

        return TrayAction.ShowWindows;
    }

    void Track(MainForm window)
    {
        windows.Insert(0, window);
        window.Activated += (_, _) =>
        {
            windows.Remove(window);
            windows.Insert(0, window);
        };
        window.FormClosed += (_, _) =>
        {
            windows.Remove(window);
            if (windows.Count == 0)
            {
                ExitThread();
            }
        };
    }

    // Another launch handed over. Its folders and files open as tabs in the windows showing
    // (see OpenInTabs), and the window they end up in comes to the front. With no window
    // showing, only ones gone to the tray, they open in a new window, as they would have in
    // that launch's own. Without folders or files, the hidden windows come back, or a new
    // window opens when none are hidden, as Win+E opens a new one in Explorer.
    void OnLaunched(string[] arguments)
    {
        var paths = global::MainForm.Openable(arguments);
        var showing = windows
            .Where(_ => _.Visible)
            .ToList();
        if (paths.Count > 0 &&
            showing.Count > 0)
        {
            OpenInTabs(showing, paths).ShowInFront();
            return;
        }

        if (paths.Count == 0 &&
            windows.Any(_ => !_.Visible))
        {
            ShowWindows();
            return;
        }

        OpenWindow(paths.Count == 0 ? [settings.StartLocation()] : paths.ToArray());
    }

    /// <summary>
    /// Opens folders and files another launch handed over, in <paramref name="windows"/>, the
    /// one used most recently first. One that a tab in any of them already shows has that tab
    /// selected, as a browser does with a link to a page already open; the rest open in new
    /// tabs in the first window. Returns the window the last of them ended up in, to bring to
    /// the front.
    /// </summary>
    internal static global::MainForm OpenInTabs(IReadOnlyList<global::MainForm> windows, IReadOnlyList<string> paths)
    {
        var last = windows[0];
        foreach (var path in paths)
        {
            if (windows.FirstOrDefault(_ => _.SelectTabShowing(path)) is { } showing)
            {
                last = showing;
                continue;
            }

            last = windows[0];
            last.AddTab(path, true);
        }

        return last;
    }

    void OpenWindow(string[] paths)
    {
        var window = new MainForm(paths, settings, places);
        Track(window);
        window.ShowInFront();
    }

    void ShowWindows()
    {
        if (windows.Count == 0)
        {
            OpenWindow([settings.StartLocation()]);
            return;
        }

        // The one used least recently first, so the one used last ends up in front. A copy,
        // since each window coming to the front moves it up the list.
        foreach (var window in windows.AsEnumerable().Reverse().ToList())
        {
            window.ShowInFront();
        }
    }

    void Exit()
    {
        global::MainForm.HideLastWindowOnClose = false;
        tray.Visible = false;
        foreach (var window in windows.ToList())
        {
            window.Close();
        }

        // Closing the last window ends the thread; this covers there being none.
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            global::MainForm.WindowOpened -= Track;
            global::MainForm.ExitRequested -= Exit;
            tray.Dispose();
            trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
