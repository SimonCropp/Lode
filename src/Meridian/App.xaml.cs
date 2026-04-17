using Meridian.Services;
using Meridian.Views;

namespace Meridian;

partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        foreach (var arg in e.Args)
        {
            switch (arg)
            {
                case "--register-shell":
                    ShellRegistration.RegisterAsShell();
                    Shutdown();
                    return;
                case "--unregister-shell":
                    ShellRegistration.UnregisterAsShell();
                    Shutdown();
                    return;
            }
        }

        var startPath = e.Args.FirstOrDefault(a => !a.StartsWith('-') && Directory.Exists(a));

        var window = new MainWindow(startPath);
        window.Show();
    }
}
