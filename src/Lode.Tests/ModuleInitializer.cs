public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifyWinForms.Initialize();

        // Effectively "the same pixels", rather than Verify's looser default. The window is
        // mostly flat background, so a loose threshold would let real changes through.
        VerifierSettings.UseSsimForPng(0.9999);

        // An exception inside a window's message handler should fail the test that caused it,
        // not put up WinForms' error dialog and leave the run waiting for someone to click.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

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
        Breadcrumb.DriveName = _ => $"Local Disk ({_.TrimEnd('\\')})";

        // Every machine's accent is its own. Captures use Windows' highlight blue, which they
        // were drawn in before Lode took the accent.
        ExplorerStyle.UserAccent = () => Color.FromArgb(0x00, 0x78, 0xD7);

        // Only some machines have the Claude desktop app; the menus and captures offer Open in
        // Claude, as they would with it.
        ClaudeDesktop.IsInstalled = () => true;

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
