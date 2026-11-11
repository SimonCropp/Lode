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

        // Dates and numbers in the list follow the current culture; pin it.
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
