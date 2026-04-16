using Microsoft.Win32;

namespace Meridian.Services;

static class ShellRegistration
{
    const string winlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string shellValue = "Shell";

    internal static void RegisterAsShell(bool perUser = true)
    {
        var exePath = Environment.ProcessPath!;
        using var key = perUser
            ? Registry.CurrentUser.CreateSubKey(winlogonKey)
            : Registry.LocalMachine.OpenSubKey(winlogonKey, true)!;
        key.SetValue(shellValue, exePath);
    }

    internal static void UnregisterAsShell(bool perUser = true)
    {
        using var key = perUser
            ? Registry.CurrentUser.OpenSubKey(winlogonKey, true)
            : Registry.LocalMachine.OpenSubKey(winlogonKey, true);
        key?.DeleteValue(shellValue, false);
    }

    internal static bool IsRegisteredAsShell()
    {
        using var key = Registry.CurrentUser.OpenSubKey(winlogonKey);
        if (key?.GetValue(shellValue) is string shell)
        {
            return string.Equals(
                Path.GetFullPath(shell),
                Environment.ProcessPath,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    internal static bool IsRunningAsShell()
    {
        using var key = Registry.CurrentUser.OpenSubKey(winlogonKey);
        return key?.GetValue(shellValue) is string shell &&
               string.Equals(
                   Path.GetFullPath(shell),
                   Environment.ProcessPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsElevated() =>
        new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
