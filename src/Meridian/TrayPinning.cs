/// <summary>
/// Shows Meridian's tray icon on the taskbar rather than in the overflow behind the ^ arrow.
/// </summary>
/// <remarks>
/// Windows 11 gives apps no API for this. Each tray icon gets an entry under
/// NotifyIconSettings, created the first time the icon shows, and its IsPromoted value is what
/// Settings > Personalization > Taskbar > Other system tray icons turns on and off. Meridian sets
/// it only while it is unset, so a user who moves the icon back into the overflow keeps it there.
/// </remarks>
static class TrayPinning
{
    const string settingsPath = @"Control Panel\NotifyIconSettings";

    public static void Pin(string executablePath)
    {
        // Windows writes the entry shortly after the icon appears, so look now and once more.
        if (!TryPin(executablePath))
        {
            var timer = new Timer { Interval = 5000 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                TryPin(executablePath);
            };
            timer.Start();
        }
    }

    static bool TryPin(string executablePath)
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(settingsPath);
            if (settings == null)
            {
                return false;
            }

            var found = false;
            foreach (var name in settings.GetSubKeyNames())
            {
                using var icon = settings.OpenSubKey(name, writable: true);
                if (icon?.GetValue("ExecutablePath") is not string path || !IsSameExecutable(path, executablePath))
                {
                    continue;
                }

                found = true;
                if (icon.GetValue("IsPromoted") == null)
                {
                    icon.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                }
            }

            return found;
        }
        catch (Exception)
        {
            // Where the icon shows is a nicety.
            return false;
        }
    }

    // Windows writes some paths with a known folder's id in place of the folder, such as
    // {F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe for the Windows folder.
    static bool IsSameExecutable(string recorded, string executablePath)
    {
        if (recorded.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!recorded.StartsWith('{'))
        {
            return false;
        }

        var separator = recorded.IndexOf('\\');
        return separator > 0 &&
               executablePath.EndsWith(recorded[separator..], StringComparison.OrdinalIgnoreCase) &&
               KnownFolderPath(recorded[..separator]) is { } folder &&
               executablePath.Equals(folder + recorded[separator..], StringComparison.OrdinalIgnoreCase);
    }

    static string? KnownFolderPath(string id)
    {
        if (!Guid.TryParse(id, out var folderId) ||
            SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPointer) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pathPointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }
}
