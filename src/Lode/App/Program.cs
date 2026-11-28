static class Program
{
    /// <summary>
    /// Every window's text, a step up from Windows' 9pt default. Tests call this too, so
    /// captures match the app.
    /// </summary>
    public static void UseDefaultFont() =>
        Application.SetDefaultFont(new("Segoe UI", 10.5f));

    // Whether the launch was only folders and files to open, and the copy running took them.
    // Anything else, a link, which may ask, or a switch, which reports, is for a launch with
    // its windows set up, as is one that finds no copy running, or can't reach it.
    static bool HandedOff(CommandLine commandLine)
    {
        if (commandLine.Kind != CommandKind.Open ||
            commandLine.Links.Count > 0 ||
            commandLine.ForExplorer.Count > 0)
        {
            return false;
        }

        using var instance = new SingleInstance();
        return !instance.IsFirst &&
               instance.TryHandOff(commandLine.FullPaths());
    }

    // A launch while Lode updates, run from the store the update is replacing, is started again
    // once the update is done, with the Lode it starts running to take its folders, and goes at
    // once: running, it would hold the store open. A switch, which reports, runs as it is. Should
    // the shell that waits not start, the launch carries on as it would have.
    /// <param name="update">The update of this copy, or null for a build's output, which none
    /// replaces.</param>
    /// <param name="instance">The name of the running Lode's mutex, which this launch would hand
    /// its folders to.</param>
    internal static bool PutOffForUpdate(CommandLine commandLine, string[] arguments, ToolUpdate? update, string instance)
    {
        if (commandLine.Kind != CommandKind.Open ||
            update == null ||
            !update.IsRunning())
        {
            return false;
        }

        try
        {
            update.Defer(arguments, Environment.CurrentDirectory, instance);
            return true;
        }
        catch (Exception exception)
            when (
                exception is
                    Win32Exception or
                    InvalidOperationException)
        {
            return false;
        }
    }

    [STAThread]
    static int Main(string[] arguments)
    {
        // A launch that only hands its folders to the copy running does it before anything of
        // Windows Forms is set up, which it has no use for. With Lode in Explorer's place, that
        // is every folder another app opens, each a wait on fonts and styles nothing shows.
        var commandLine = CommandLine.Parse(arguments);
        if (PutOffForUpdate(commandLine, arguments, Updates.Find(AppContext.BaseDirectory), SingleInstance.Name()) ||
            HandedOff(commandLine))
        {
            return 0;
        }

        // What ApplicationConfiguration.Initialize does, from properties that need UseWindowsForms,
        // which a dotnet tool cannot have. See Lode.csproj.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        UseDefaultFont();

        // Each window stays hidden until it and every control in it have painted once, then
        // appears whole, rather than as a blank frame its controls fill in one at a time.
        Application.SetDefaultFormRevealMode(FormRevealMode.Deferred);

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

        // Made full before they leave this launch, which is the one that knows where a relative
        // path is from.
        var paths = commandLine.FullPaths();
        if (commandLine.Links.Count > 0)
        {
            paths = [.. paths, .. RepositoriesOf(commandLine.Links)];
        }

        // Nothing for Lode: handing over no folders would bring its windows back.
        if (paths.Count == 0 &&
            (commandLine.ForExplorer.Count > 0 || commandLine.Links.Count > 0))
        {
            return 0;
        }
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

        // Run as administrator, Lode is a copy of its own beside the one that isn't, with
        // windows of its own, whose place and tabs it keeps apart.
        var settings = Settings.Load(Settings.DefaultPath, administrator: Environment.IsPrivilegedProcess);

        // Before the first window, so starting with the defaults is not mistaken for tabs and
        // options lost.
        if (settings.LoadProblem != null)
        {
            MessageBoxes.Show(settings.LoadProblem, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (settings.LoadBug != null)
        {
            IssueLauncher.LaunchForException("Failed to load settings", settings.LoadBug);
        }

        // The code folder gone, nothing in it gets a Git menu or state, which would otherwise
        // only look as though Lode had lost them.
        if (settings.MissingCodeDirectory() is { } missing)
        {
            MessageBoxes.Show(
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
        Application.ThreadException += (_, args) =>
            IssueLauncher.LaunchForException(args.Exception.Message, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                IssueLauncher.LaunchForException(exception.Message, exception);
            }
        };

        // One a task threw that nothing awaited, such as a tab's load, which reaches neither of
        // the above and was otherwise lost. Told when the task is collected, on the finalizer
        // thread, so asked from another.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            foreach (var exception in args.Exception.Flatten().InnerExceptions)
            {
                IssueLauncher.LaunchForExceptionLater(exception.Message, exception);
            }
        };

        // What was extracted from zips to be opened, copied or dragged more than a day ago.
        _ = Task.Run(Zip.ClearOldTemp);
        _ = Task.Run(ReplaceInFiles.ClearOldTemp);

        JumpList.Follow(places, settings);
        Application.Run(new WindowContext(MainForm.ForStart(paths.ToArray(), settings, places), settings, places, instance));
        return 0;
    }

    // The clone in the code folder of each repository the links name, or the file in it a link
    // names. A repository not there is offered to be cloned there, or the link to the app it was
    // for, as Windows would have handed it on before Lode took such links over. A link Lode
    // can't read goes to that app.
    static List<string> RepositoriesOf(IReadOnlyList<string> links)
    {
        Settings.SeedFromInstalled();
        var codeFolder = CodeFolderForLinks(Settings.DefaultPath);
        var repositoryLinks = RepositoryLinks.ForCurrentUser();
        var found = new List<string>();
        foreach (var text in links)
        {
            if (RepositoryLink.Parse(text) is not { } link)
            {
                // Another of the app's links, GitHub Desktop's sign-in say: the app's to open,
                // without asking, as Windows would have handed it on. The browser asked before
                // opening it, the app's sign-in waits on it, and a question from Lode would be
                // one nobody could tell the answer to. It reaches the app as the one argument it
                // was, whatever it holds, so it can't give the app arguments of its own.
                var handler = RepositoryLinks.HandlerOf(text)!;
                if (!new RepositoryLink(handler, text, "", null).Forward(repositoryLinks))
                {
                    MessageBoxes.Show(
                        $"Lode can't tell which repository this link is for, and can't find {handler.App} to hand it to:{Environment.NewLine}{Environment.NewLine}{text}",
                        "Lode",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                continue;
            }

            if (codeFolder != null &&
                link.Find(codeFolder) is { } path)
            {
                found.Add(path);
                continue;
            }

            if (NotInCodeFolder(link, codeFolder, repositoryLinks) is { } cloned)
            {
                found.Add(cloned);
            }
        }

        return found;
    }

    // Read without setting a corrupt file aside or saying so, which is left to the start's load,
    // should this launch be the start, for the user to be told, once, as at any start.
    internal static string? CodeFolderForLinks(string settingsPath) =>
        Settings.CodeDirectoryAt(settingsPath);

    // Cloned into the code folder, or handed to the app the link was for, which can clone it
    // elsewhere. The clone, or the file in it the link names, once cloned.
    static string? NotInCodeFolder(RepositoryLink link, string? codeFolder, RepositoryLinks repositoryLinks)
    {
        if (codeFolder != null &&
            Directory.Exists(codeFolder))
        {
            return RepositoryClone.Offer(link, codeFolder, repositoryLinks);
        }

        var app = link.Handler.App;
        var why = codeFolder == null
            ? $"No code folder is chosen, so Lode can't look for {link.Name}. Choose one on the Code page of Options."
            : $"Lode can't find the code folder, {codeFolder}, to look for {link.Name} in. Choose another on the Code page of Options.";
        if (repositoryLinks.ForwardCommand(link.Handler) == null)
        {
            MessageBoxes.Show(
                $"{why}{Environment.NewLine}{Environment.NewLine}{app} isn't there to open the link instead.",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        var answer = MessageBoxes.Show(
            $"{why}{Environment.NewLine}{Environment.NewLine}Open the link in {app} instead?",
            "Lode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes &&
            !link.Forward(repositoryLinks))
        {
            MessageBoxes.Show($"{app} wouldn't start.", "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return null;
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
            MessageBoxes.Show(
                message,
                "Lode",
                MessageBoxButtons.OK,
                failed ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            return;
        }

        (failed ? Console.Error : Console.Out).WriteLine(message);
    }
}
