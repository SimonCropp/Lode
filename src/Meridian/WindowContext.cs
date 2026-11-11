/// <summary>
/// The running app: its windows and its tray icon. Closing the last window hides it to the
/// tray rather than exiting; only Exit (in the tray menu or the window menu), or Windows
/// shutting down, ends the app.
/// </summary>
sealed class WindowContext : ApplicationContext
{
    HashSet<MainForm> windows = [];
    Settings settings;
    Places places;
    NotifyIcon tray;
    Icon trayIcon = ShellIcons.FolderWindowIcon();

    public WindowContext(MainForm first, Settings settings, Places places, SingleInstance instance)
    {
        this.settings = settings;
        this.places = places;

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Meridian", null, (_, _) => ShowWindows());
        open.Font = new(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add("New window", null, (_, _) => OpenWindow([settings.StartLocation()]));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new()
        {
            Icon = trayIcon,
            Text = "Meridian",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ShowWindows();
            }
        };

        TrayPinning.Pin(Environment.ProcessPath!);

        global::MainForm.HideLastWindowOnClose = true;
        global::MainForm.WindowOpened += Track;
        global::MainForm.ExitRequested += Exit;
        Track(first);
        first.Show();
        instance.Listen(OnLaunched);
        WarmUpContextMenu();
    }

    // Once the first window has drawn and settled, so the work does not hold up opening it.
    static void WarmUpContextMenu()
    {
        var timer = new Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            ShellContextMenu.WarmUp();
        };
        timer.Start();
    }

    void Track(MainForm window)
    {
        windows.Add(window);
        window.FormClosed += (_, _) =>
        {
            windows.Remove(window);
            if (windows.Count == 0)
            {
                ExitThread();
            }
        };
    }

    // Another launch handed over. With folders or files, they open in a new window, as they
    // would have in that launch's own. Without, the hidden windows come back, or a new window
    // opens when none are hidden.
    void OnLaunched(string[] paths)
    {
        if (paths.Length == 0 &&
            windows.Any(_ => !_.Visible))
        {
            ShowWindows();
            return;
        }

        OpenWindow(paths.Length == 0 ? [settings.StartLocation()] : paths);
    }

    void OpenWindow(string[] paths)
    {
        var window = new MainForm(paths, settings, places);
        Track(window);
        window.Show();
        window.Activate();
    }

    void ShowWindows()
    {
        if (windows.Count == 0)
        {
            OpenWindow([settings.StartLocation()]);
            return;
        }

        foreach (var window in windows)
        {
            window.Show();
            if (window.WindowState == FormWindowState.Minimized)
            {
                window.WindowState = FormWindowState.Normal;
            }

            window.Activate();
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
