public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifyWinForms.Initialize();

        VerifierSettings.Inline();

        // Effectively "the same pixels", rather than Verify's looser default. The window is
        // mostly flat background, so a loose threshold would let real changes through.
        VerifierSettings.UseSsimForPng(0.9999);

        // An exception inside a window's message handler fails the test that caused it, rather
        // than putting up WinForms' error dialog: WindowThread sees to that, on each test's
        // thread. Likewise an error Lode would offer an issue for: a bug, which fails the test
        // rather than asking, in a box nobody is there to answer.
        IssueLauncher.Declined = text => throw new(text);

        // And settings a test could not save, which fail it too.
        Settings.Unsaved = (path, exception) => throw new($"Could not save settings to {path}", exception);

        // Unscaled, so captures describe the app rather than the display of whoever ran them.
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Program.UseDefaultFont();

        // The app's Debug builds mark their window titles; tests run in both builds.
        MainForm.TitleSuffix = "";

        // The ring beside a running action stands still, so captures of it match each run.
        ActionsPanel.Clock = () => 0;

        // Every machine's drives are called something else; the address bar's are all the same.
        Breadcrumb.DriveName = (drive, _) => $"Local Disk ({drive.TrimEnd('\\')})";

        // Drives are described as they are asked about, rather than off the UI thread, so the
        // tree and This PC show them described by the time they are captured. How one shows until
        // it has been is tested on its own.
        ShellIcons.DescribeOffUiThread = drive => Task.FromResult(ShellIcons.Describe(drive));

        // Every machine's accent is its own. Captures use Windows' highlight blue, which they
        // were drawn in before Lode took the accent.
        ExplorerStyle.UserAccent = () => Color.FromArgb(0x00, 0x78, 0xD7);

        // Only some machines have the Claude desktop app; the menus and captures offer Open in
        // Claude, as they would with it.
        ClaudeDesktop.IsInstalled = () => true;

        // Likewise the coding agents: two, one a program and one as npm installs them.
        CodingAgents.Installed = () =>
        [
            new("Claude Code", @"C:\Agents\claude.exe"),
            new("Codex CLI", @"C:\Agents\codex.cmd")
        ];

        // What tests do to files would otherwise show as Undo in each other's menus.
        UndoHistory.Instance = new(new UndoHistory.ShellFileActions())
        {
            Records = false
        };

        // Dates and numbers in the list follow the current culture; pin it.
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
