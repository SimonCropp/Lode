static class Program
{
    /// <summary>
    /// Every window's text, a step up from Windows' 9pt default. Tests call this too, so
    /// captures match the app.
    /// </summary>
    public static void UseDefaultFont() =>
        Application.SetDefaultFont(new("Segoe UI", 10.5f));

    [STAThread]
    static int Main(string[] arguments)
    {
        // What ApplicationConfiguration.Initialize does, from properties that need UseWindowsForms,
        // which a dotnet tool cannot have. See Lode.csproj.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        UseDefaultFont();

        // Each window stays hidden until it and every control in it have painted once, then
        // appears whole, rather than as a blank frame its controls fill in one at a time.
        Application.SetDefaultFormRevealMode(FormRevealMode.Deferred);

        var commandLine = CommandLine.Parse(arguments);
        switch (commandLine.Kind)
        {
            case CommandKind.ReplaceExplorer:
                return Change(
                    _ => _.Replace(),
                    "Folders, drives and Win+E now open in Lode. To undo: lode --restore-explorer");
            case CommandKind.RestoreExplorer:
                return Change(
                    _ => _.Restore(),
                    "Folders, drives and Win+E open in File Explorer again.");
            case CommandKind.Help when commandLine.Error != null:
                Report($"{commandLine.Error}{Environment.NewLine}{Environment.NewLine}{CommandLine.Usage}", failed: true);
                return 1;
            case CommandKind.Help:
                Report(CommandLine.Usage, failed: false);
                return 0;
        }

        foreach (var folder in commandLine.ForExplorer)
        {
            OpenInExplorer(folder);
        }

        // Nothing for Lode: handing over no folders would bring its windows back.
        if (commandLine is { Paths.Count: 0, ForExplorer.Count: > 0 })
        {
            return 0;
        }

        // Made full before they leave this launch, which is the one that knows where a relative
        // path is from.
        var paths = commandLine.FullPaths();
        using var instance = new SingleInstance();
        if (!instance.IsFirst &&
            instance.TryHandOff(paths))
        {
            return 0;
        }

        instance.Listen();

        // Behind the window as it starts: the PATH is looked through once a session.
        CodingAgents.StartLooking();

        // Lode stays running, in the tray, and Windows lets nobody rename or delete the folder
        // a program is running in. So not the one this launch happened to be started in.
        Environment.CurrentDirectory = Environment.SystemDirectory;
        ShellNew.Warm();
        Settings.SeedFromInstalled();
        var settings = Settings.Load(Settings.DefaultPath);

        // Before the first window, so starting with the defaults is not mistaken for tabs and
        // options lost.
        if (settings.LoadProblem != null)
        {
            MessageBox.Show(settings.LoadProblem, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (settings.LoadBug != null)
        {
            IssueLauncher.LaunchForException("Failed to load settings", settings.LoadBug);
        }

        // The code folder gone, nothing in it gets a Git menu or state, which would otherwise
        // only look as though Lode had lost them.
        if (settings.MissingCodeDirectory() is { } missing)
        {
            MessageBox.Show(
                $"Lode can't find the code folder:{Environment.NewLine}{Environment.NewLine}" +
                $"{missing}{Environment.NewLine}{Environment.NewLine}" +
                "It may have been renamed, moved or deleted. Until another is chosen on the Code " +
                "page of Options, folders get no Git menu or Git column.",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // Before the first window, which takes its colours from it.
        ExplorerStyle.Use(settings.Theme);

        // Before the first window, which shows a pending update from the start.
        Updates.Instance = Updates.ForThisCopy(settings);
        var places = Places.ForThisMachine(settings);

        // An exception nothing caught offers an issue for it, in place of WinForms' own error
        // dialog. One on the UI thread leaves Lode running; one off it ends Lode once answered.
        Application.ThreadException += (_, eventArgs) =>
            IssueLauncher.LaunchForException(eventArgs.Exception.Message, eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                IssueLauncher.LaunchForException(exception.Message, exception);
            }
        };

        // One a task threw that nothing awaited, such as a tab's load, which reaches neither of
        // the above and was otherwise lost. Told when the task is collected, on the finalizer
        // thread, so asked from another.
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            eventArgs.SetObserved();
            foreach (var exception in eventArgs.Exception.Flatten().InnerExceptions)
            {
                IssueLauncher.LaunchForExceptionLater(exception.Message, exception);
            }
        };

        // What was extracted from zips to be opened, copied or dragged more than a day ago.
        _ = Task.Run(Zip.ClearOldTemp);

        JumpList.Follow(places, settings);
        Application.Run(new WindowContext(new(paths.ToArray(), settings, places), settings, places, instance));
        return 0;
    }

    // Explorer opens a folder named on its command line itself, not through the explore verb,
    // so this never comes back to Lode.
    static void OpenInExplorer(string folder)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            using var process = Process.Start(explorer, [folder]);
        }
        catch (Exception)
        {
            // Explorer will not start: nothing else would show the folder either.
        }
    }

    static int Change(Action<ExplorerReplacement> change, string done)
    {
        try
        {
            change(ExplorerReplacement.ForCurrentUser());
        }
        catch (Exception exception)
        {
            Report(exception.Message, failed: true);
            return 1;
        }

        Report(done, failed: false);
        return 0;
    }

    // A GUI app has no console of its own. Started from a terminal, or with its output
    // redirected, it writes there; started any other way (Win+R, a shortcut) it shows a message.
    static void Report(string message, bool failed)
    {
        if (AttachConsole(AttachParentProcess))
        {
            // A terminal does not wait for a GUI app, so its prompt is already on the line.
            Console.WriteLine();
        }
        else if (GetStdHandle(StandardOutputHandle) is 0 or -1)
        {
            MessageBox.Show(
                message,
                "Lode",
                MessageBoxButtons.OK,
                failed ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            return;
        }

        (failed ? Console.Error : Console.Out).WriteLine(message);
    }
}
