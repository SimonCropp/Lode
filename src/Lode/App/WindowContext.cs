/// <summary>
/// The running app: its windows and its tray icon. Closing the last window hides it to the
/// tray rather than exiting; only Exit (in the tray menu or the window menu), an update, or
/// Windows shutting down, ends the app.
/// </summary>
sealed class WindowContext : ApplicationContext
{
    // The window used most recently first, which is where another launch's folders go.
    List<MainForm> windows = [];
    Settings settings;
    Places places;
    NotifyIcon tray;
    Icon trayIcon = ShellIcons.FolderWindowIcon();

    // One for every window, so no repository is fetched twice at once from two.
    BackgroundFetch backgroundFetch;

    // After the tray icon's tooltip and its menu's header: " (Debug)" in a Debug build, so its
    // icon is easy to tell from the installed Lode's beside it, as its window titles are.
    const string buildSuffix =
#if DEBUG
        " (Debug)";
#else
        "";
#endif

    public WindowContext(MainForm first, Settings settings, Places places, SingleInstance instance)
    {
        this.settings = settings;
        this.places = places;

        var menu = new ExplorerMenu();
        var open = new ToolStripMenuItem("Open Lode" + buildSuffix, null, (_, _) => ShowWindows());
        open.Font = new(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add("New window", null, (_, _) => OpenWindow([settings.StartLocation()]));
        menu.Items.Add(new ToolStripSeparator());
        var update = new ToolStripMenuItem("", null, (_, _) => OnUpdateItem());
        menu.Items.Add(update);
        menu.Items.Add("Exit", null, (_, _) => Exit());
        // Looked at each time the menu opens, rather than as checks end, since the menu is only
        // seen then.
        menu.Opening += (_, _) =>
        {
            var text = UpdateItemText(Updates.Instance);
            update.Visible = text != null;
            update.Text = text;
            update.Enabled = !Updates.Instance.Checking;
        };
        tray = new()
        {
            Icon = trayIcon,
            Text = "Lode" + buildSuffix,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, args) =>
        {
            switch (TrayClick(args.Button, Control.ModifierKeys))
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
        ShellWindows.Enabled = true;
        global::MainForm.WindowOpened += Track;
        global::MainForm.ExitRequested += Exit;
        Track(first);
        first.Show();
        instance.HandTo(OnLaunched);
        ReportUpdate();
        Updates.Instance.Start();
        backgroundFetch = new(settings);
        backgroundFetch.Start();
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

    /// <summary>
    /// The tray menu's update item: the update once a newer Lode is known, otherwise a check for
    /// one, which is off while it runs. Null for none, which is for a build's output, as it has
    /// nothing to update.
    /// </summary>
    internal static string? UpdateItemText(Updates updates)
    {
        if (updates.Tool == null)
        {
            return null;
        }

        if (updates.Checking)
        {
            return "Checking for updates…";
        }

        if (updates.Available is { } available)
        {
            return $"Update to Lode {available.ToNormalizedString()}";
        }

        return "Check for updates";
    }

    void OnUpdateItem()
    {
        if (Updates.Instance.Available != null)
        {
            ApplyUpdate();
            return;
        }

        _ = CheckForUpdates();
    }

    // The menu is gone by the time NuGet answers, so a balloon says what it found.
    async Task CheckForUpdates()
    {
        await Updates.Instance.Check();
        // Not once Lode is exiting, which hides the icon.
        if (!tray.Visible)
        {
            return;
        }

        var notice = CheckNotice(Updates.Instance);
        tray.ShowBalloonTip(10000, notice.Title, notice.Message, notice.Icon);
    }

    /// <summary>
    /// What the tray says once a check from its menu ends.
    /// </summary>
    internal static UpdateNotice CheckNotice(Updates updates)
    {
        if (updates.Available is { } available)
        {
            var version = available.ToNormalizedString();
            return new(
                $"Lode {version} is available",
                $"Update to Lode {version} is in this icon's menu.",
                ToolTipIcon.Info);
        }

        if (updates.Error is { } error)
        {
            return new(
                "Couldn't check for updates",
                error,
                ToolTipIcon.Warning);
        }

        return new(
            "Lode is up to date",
            $"Lode {updates.Current.ToNormalizedString()} is the newest version.",
            ToolTipIcon.Info);
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
    internal static MainForm OpenInTabs(IReadOnlyList<MainForm> windows, IReadOnlyList<string> paths)
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
        if (!MayStopActions("Exit Lode"))
        {
            return;
        }

        CloseAll();
    }

    // The update waits for Lode to exit, so it goes as Exit does, once the shell is started.
    void ApplyUpdate()
    {
        if (!MayStopActions("Update Lode"))
        {
            return;
        }

        try
        {
            Updates.Instance.Apply();
        }
        catch (Exception exception)
            when (
                exception is
                    Win32Exception or
                    InvalidOperationException)
        {
            MessageBox.Show(
                windows.FirstOrDefault(_ => _.Visible),
                $"Lode couldn't start the update: {exception.Message}",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        CloseAll();
    }

    // Asked first, as exiting stops what is running part way. True when nothing is running, or
    // the answer is to go anyway.
    bool MayStopActions(string doing)
    {
        if (ActionLog.RunningMessage() is not { } running)
        {
            return true;
        }

        var answer = MessageBox.Show(
            windows.FirstOrDefault(_ => _.Visible),
            $"{running}\n\n{doing} anyway?",
            doing,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    void CloseAll()
    {
        global::MainForm.HideLastWindowOnClose = false;
        tray.Visible = false;
        // Each window writes its tabs and bounds to the settings as it closes, and the last to
        // write is what the next start restores. So the one used least recently first, and the
        // one used last, last.
        foreach (var window in windows.AsEnumerable().Reverse().ToList())
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
            backgroundFetch.Dispose();
            tray.Dispose();
            trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
