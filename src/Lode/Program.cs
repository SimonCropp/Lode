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

        using var instance = new SingleInstance();
        if (!instance.IsFirst &&
            instance.TryHandOff(commandLine.Paths))
        {
            return 0;
        }

        var settings = Settings.Load(Settings.DefaultPath);
        var places = Places.ForThisMachine();
        Application.Run(new WindowContext(new(commandLine.Paths.ToArray(), settings, places), settings, places, instance));
        return 0;
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
