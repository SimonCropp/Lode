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
        // rather than asking, in a box nobody is there to answer. Thrown where the box would have
        // been put up, as it would have stopped there, and kept for the test, should what it is
        // thrown to swallow it.
        IssueLauncher.Declined = text => throw WindowThreadExecutor.Keep(new(text));

        // And settings a test could not save, which fail it too.
        Settings.Unsaved = (path, exception) => throw WindowThreadExecutor.Keep(new($"Could not save settings to {path}", exception));

        // And any other box Lode puts up, which fails the test with what it says, rather than
        // waiting for a click. A test that expects a box answers it through the stand-in Lode
        // asks it through, and never reaches here.
        MessageBoxes.Showing = (_, text, caption, _, _, _) =>
            throw WindowThreadExecutor.Keep(new($"Lode put up a message box, which nobody in a test run is there to answer. {caption}: {text}"));
        MessageBoxes.ShowingDialog = (_, page, _) =>
            throw WindowThreadExecutor.Keep(new($"Lode put up a task dialog, which nobody in a test run is there to answer. {page.Caption}: {page.Heading} {page.Text}"));

        // And the shell's dialog asking what to do with each file an extract finds there
        // already. A test that extracts over something answers through a stand-in.
        Zip.MoveIntoPlace = (_, _, destination) =>
            throw new InvalidOperationException($"Lode moved what it extracted into {destination} through the shell, whose dialog nobody in a test run is there to answer.");

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

        // The list's dates follow the region's order, but day first in the tests, as the captures,
        // and the readme's pictures made of them, have always shown them. FormattingTests tries
        // other cultures.
        var dayFirst = CultureInfo.GetCultureInfo("en-GB");
        Formatting.DateCulture = () => dayFirst;
    }
}
