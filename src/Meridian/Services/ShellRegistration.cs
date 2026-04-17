using Microsoft.Win32;

namespace Meridian.Services;

public static class ShellRegistration
{
    const string winlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string shellValue = "Shell";
    const string defaultShell = "explorer.exe";

    internal static void RegisterAsShell(bool perUser = true)
    {
        var exePath = Environment.ProcessPath!;
        using var key = perUser
            ? Registry.CurrentUser.CreateSubKey(winlogonKey)
            : Registry.LocalMachine.CreateSubKey(winlogonKey);
        key.SetValue(shellValue, exePath);
    }

    internal static void UnregisterAsShell(bool perUser = true)
    {
        if (perUser)
        {
            using var key = Registry.CurrentUser.OpenSubKey(winlogonKey, true);
            key?.DeleteValue(shellValue, false);
        }
        else
        {
            using var key = Registry.LocalMachine.OpenSubKey(winlogonKey, true);
            key?.SetValue(shellValue, defaultShell);
        }
    }

    internal static bool IsRegisteredAsShell()
    {
        var processPath = Environment.ProcessPath;

        using var hkcuKey = Registry.CurrentUser.OpenSubKey(winlogonKey);
        if (hkcuKey?.GetValue(shellValue) is string hkcuShell)
        {
            return string.Equals(
                Path.GetFullPath(hkcuShell),
                processPath,
                StringComparison.OrdinalIgnoreCase);
        }

        using var hklmKey = Registry.LocalMachine.OpenSubKey(winlogonKey);
        if (hklmKey?.GetValue(shellValue) is string hklmShell)
        {
            return string.Equals(
                Path.GetFullPath(hklmShell),
                processPath,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    internal static bool IsElevated() =>
        new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
