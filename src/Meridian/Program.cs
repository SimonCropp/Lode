static class Program
{
    [STAThread]
    static void Main(string[] arguments)
    {
        ApplicationConfiguration.Initialize();
        var settings = Settings.Load(Settings.DefaultPath);
        Application.Run(new MainForm(arguments, settings, Places.ForThisMachine()));
    }
}
