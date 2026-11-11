using Microsoft.Win32;

/// <summary>
/// Makes Meridian, rather than File Explorer, what opens folders and drives (double-clicks,
/// folder shortcuts, other apps asking the shell to open a folder) and what Win+E starts.
/// </summary>
/// <remarks>
/// Writes only to the current user's classes, so it needs no elevation and other accounts keep
/// Explorer. Explorer is still the shell: its taskbar button, and apps that start explorer.exe
/// themselves or ask it to show a file selected, still open Explorer. Explorer's own Open verb
/// stays on every folder's right-click menu.
/// </remarks>
sealed class ExplorerReplacement(string classesPath, string command, bool notifyShell = false)
{
    // The verb added to folders and drives, and made their default.
    const string verb = "Meridian";

    // Where Replace keeps a default verb it displaced, so Restore can put it back.
    const string previousDefaultName = "MeridianPreviousDefault";

    // The shell object Win+E opens. A command here, with an empty DelegateExecute, runs in
    // place of Explorer's own handler.
    const string winEPath = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow";

    // Marks the Win+E key as Meridian's, so Restore never removes another tool's.
    const string ownerName = "CreatedBy";

    // Directory covers every file system folder; Drive covers drive roots. Folder is left alone:
    // it also covers virtual folders (Control Panel, Recycle Bin) that Meridian cannot show.
    static readonly string[] folderClasses = ["Directory", "Drive"];

    public static ExplorerReplacement ForCurrentUser() =>
        new(@"Software\Classes", PreferredCommand(), notifyShell: true);

    /// <summary>
    /// True when folders open in a Meridian, this copy or another.
    /// </summary>
    public bool IsActive
    {
        get
        {
            using var shell = Registry.CurrentUser.OpenSubKey($@"{classesPath}\Directory\shell");
            return shell?.GetValue("") as string == verb;
        }
    }

    /// <summary>
    /// The Meridian folders open in, as a command without the folder argument, or null when
    /// folders do not open in Meridian.
    /// </summary>
    public string? RegisteredCommand
    {
        get
        {
            if (!IsActive)
            {
                return null;
            }

            using var key = Registry.CurrentUser.OpenSubKey($@"{classesPath}\Directory\shell\{verb}\command");
            if (key?.GetValue("") is not string folderCommand)
            {
                return null;
            }

            return folderCommand.EndsWith(folderArgument, StringComparison.Ordinal)
                ? folderCommand[..^folderArgument.Length]
                : folderCommand;
        }
    }

    /// <summary>
    /// True when folders open in the copy of Meridian that Replace registers (the installed
    /// tool, or this copy when there is none), rather than in Explorer or some other copy.
    /// </summary>
    public bool IsActiveHere => RegisteredCommand == command;

    const string folderArgument = " \"%1\"";

    string FolderCommand => command + folderArgument;

    public void Replace()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(classesPath);
        foreach (var folderClass in folderClasses)
        {
            using var shell = classes.CreateSubKey($@"{folderClass}\shell");
            using var verbKey = shell.CreateSubKey(verb);
            if (shell.GetValue("") is not string previous)
            {
                verbKey.DeleteValue(previousDefaultName, throwOnMissingValue: false);
            }
            else if (previous != verb)
            {
                verbKey.SetValue(previousDefaultName, previous);
            }

            verbKey.SetValue("", "Open in Meridian");
            using (var commandKey = verbKey.CreateSubKey("command"))
            {
                commandKey.SetValue("", FolderCommand);
            }

            shell.SetValue("", verb);
        }

        using (var winE = classes.CreateSubKey(winEPath))
        {
            winE.SetValue(ownerName, verb);
            using var commandKey = winE.CreateSubKey("command");
            commandKey.SetValue("", command);
            commandKey.SetValue("DelegateExecute", "");
        }

        NotifyShell();
    }

    public void Restore()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(classesPath, writable: true);
        if (classes == null)
        {
            return;
        }

        foreach (var folderClass in folderClasses)
        {
            var shellPath = $@"{folderClass}\shell";
            using (var shell = classes.OpenSubKey(shellPath, writable: true))
            {
                if (shell == null)
                {
                    continue;
                }

                string? previous;
                using (var verbKey = shell.OpenSubKey(verb))
                {
                    previous = verbKey?.GetValue(previousDefaultName) as string;
                }

                if (shell.GetValue("") as string == verb)
                {
                    if (previous == null)
                    {
                        shell.DeleteValue("", throwOnMissingValue: false);
                    }
                    else
                    {
                        shell.SetValue("", previous);
                    }
                }

                shell.DeleteSubKeyTree(verb, throwOnMissingSubKey: false);
            }

            DeleteEmptyKeys(classes, shellPath);
        }

        bool ownsWinE;
        using (var winE = classes.OpenSubKey(winEPath))
        {
            ownsWinE = winE?.GetValue(ownerName) as string == verb;
        }

        if (ownsWinE)
        {
            classes.DeleteSubKeyTree(winEPath);
            DeleteEmptyKeys(classes, Path.GetDirectoryName(winEPath)!);
        }

        NotifyShell();
    }

    // Deletes the key at path, then each parent in turn, for as long as they are empty. Stops
    // short of the classes key itself.
    static void DeleteEmptyKeys(RegistryKey classes, string path)
    {
        while (path.Length > 0)
        {
            using (var key = classes.OpenSubKey(path))
            {
                if (key is { SubKeyCount: > 0 } or { ValueCount: > 0 })
                {
                    return;
                }
            }

            classes.DeleteSubKey(path, throwOnMissingSubKey: false);
            path = Path.GetDirectoryName(path) ?? "";
        }
    }

    // Tells Explorer the associations changed, so it stops using what it has cached.
    void NotifyShell()
    {
        if (notifyShell)
        {
            SHChangeNotify(ShellChangeAssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // Quoted, ready for a registry command. The installed dotnet tool when there is one, even
    // when Replace is clicked in a Debug build or other copy: its shim
    // (%USERPROFILE%\.dotnet\tools\meridian.exe) keeps its path across tool updates, unlike the
    // versioned copy under tools\.store, and unlike a build folder that gets cleaned or moved.
    // Only without the tool is this process registered.
    static string PreferredCommand()
    {
        if (InstalledToolPath() is { } tool)
        {
            return $"\"{tool}\"";
        }

        var processPath = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started as "dotnet Meridian.dll". This path is version specific, but this only
            // happens when someone starts Meridian through dotnet themselves, never as the tool.
            return $"\"{processPath}\" \"{typeof(ExplorerReplacement).Assembly.Location}\"";
        }

        return $"\"{processPath}\"";
    }

    // Where "dotnet tool install -g" puts the shim: .dotnet\tools under DOTNET_CLI_HOME when that
    // is set, otherwise under the user profile.
    static string? InstalledToolPath()
    {
        var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var path = Path.Combine(home, ".dotnet", "tools", "meridian.exe");
        return File.Exists(path) ? path : null;
    }
}
