/// <summary>
/// Makes Lode, rather than File Explorer, what opens folders and drives (double-clicks,
/// folder shortcuts, other apps asking the shell to open or explore a folder) and what Win+E
/// starts.
/// </summary>
/// <remarks>
/// Writes only to the current user's classes, so it needs no elevation and other accounts keep
/// Explorer. Explorer is still the shell: its taskbar button, and apps that start explorer.exe
/// themselves, still open Explorer. Apps that ask the shell to show a file selected find Lode's
/// tabs, which register with it while this is in place (see <see cref="ShellWindows"/>).
/// Explorer's own Open verb stays on every folder's right-click menu. The explore verb on Folder brings Lode virtual
/// folders too, which it passes on to Explorer (see <see cref="CommandLine.ForExplorer"/>).
/// </remarks>
sealed class ExplorerReplacement(string classesPath, string command, bool notifyShell = false)
{
    // The verb added to folders and drives, and made their default.
    const string verb = "Lode";

    // Where Replace keeps a default verb it displaced, so Restore can put it back.
    const string previousDefaultName = "LodePreviousDefault";

    // The verb apps name when they want a folder shown in a file manager, rather than whatever
    // the default verb does: Chromium and Electron apps use it to open a folder (GitHub
    // Desktop's Show in Explorer, for one), as does ShellExecute with "explore".
    const string exploreVerb = "explore";

    // The shell object Win+E opens. A command here, with an empty DelegateExecute, runs in
    // place of Explorer's own handler.
    const string winEPath = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow";

    // Marks the keys Replace adds where another tool could have its own (Win+E, explore) as
    // Lode's, so Restore never removes another tool's.
    const string ownerName = "CreatedBy";

    // The handler Explorer's own verbs name in their DelegateExecute. An explore verb in the
    // user's classes naming it is a copy of Explorer's (some tools leave one there when they hand
    // folders back to Explorer), not another file manager's, so Replace takes it over.
    const string explorerHandler = "{11dbb47c-a525-400b-9e80-a54615a090c0}";

    // Where Replace keeps, on the command key of a copy of Explorer's explore verb it took over,
    // what that command had, so Restore can give the verb back to Explorer.
    const string previousHandlerName = "LodePreviousDelegateExecute";
    const string previousCommandName = "LodePreviousCommand";

    // Where Lode is made the default verb. Directory covers every file system folder; Drive
    // covers drive roots. Folder is left alone: it also covers virtual folders (Control Panel,
    // Recycle Bin) that Lode cannot show.
    static string[] folderClasses = ["Directory", "Drive"];

    // Where the explore verb is added. An app that names just a folder finds the one on
    // Directory or Drive before Explorer's own on Folder. Chromium, and so Electron, names the
    // Folder class outright as well, which finds only one on Folder. That one is asked for
    // virtual folders too, and Lode passes those on to Explorer.
    static string[] exploreClasses = ["Directory", "Drive", "Folder"];

    public static ExplorerReplacement ForCurrentUser() =>
        new(@"Software\Classes", PreferredCommand(), notifyShell: true);

    /// <summary>
    /// Where Replace writes, under the current user's registry hive.
    /// </summary>
    internal string ClassesPath => classesPath;

    /// <summary>
    /// The Lode Replace registers, as a command without the folder argument.
    /// </summary>
    internal string Command => command;

    /// <summary>
    /// Whether changes are told to Explorer: only for the real classes.
    /// </summary>
    internal bool NotifiesShell => notifyShell;

    /// <summary>
    /// True when folders open in a Lode, this copy or another.
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
    /// The Lode folders open in, as a command without the folder argument, or null when
    /// folders do not open in Lode.
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

            if (folderCommand.EndsWith(folderArgument, StringComparison.Ordinal))
            {
                return folderCommand[..^folderArgument.Length];
            }

            return folderCommand;
        }
    }

    /// <summary>
    /// True when folders open in the copy of Lode that Replace registers (the installed
    /// tool, or this copy when there is none), rather than in Explorer or some other copy.
    /// </summary>
    public bool IsActiveHere => RegisteredCommand == command;

    /// <summary>
    /// True when Replace has nothing left to add for this copy: apps that ask for the explore
    /// verb get it too, unless another tool has that verb, which Replace leaves alone. False
    /// after a Replace by a Lode from before it added the verb, or added it on Folder, or took
    /// over a copy of Explorer's.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            if (!IsActiveHere)
            {
                return false;
            }

            foreach (var exploreClass in exploreClasses)
            {
                using var explore = Registry.CurrentUser.OpenSubKey($@"{classesPath}\{exploreClass}\shell\{exploreVerb}");
                if (explore == null)
                {
                    return false;
                }

                if (explore.GetValue(ownerName) as string != verb)
                {
                    if (IsExplorers(explore))
                    {
                        return false;
                    }

                    continue;
                }

                using var commandKey = explore.OpenSubKey("command");
                if (commandKey?.GetValue("") as string != FolderCommand)
                {
                    return false;
                }
            }

            return true;
        }
    }

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

            verbKey.SetValue("", "Open in Lode");
            using (var commandKey = verbKey.CreateSubKey("command"))
            {
                commandKey.SetValue("", FolderCommand);
            }

            shell.SetValue("", verb);
        }

        foreach (var exploreClass in exploreClasses)
        {
            using var shell = classes.CreateSubKey($@"{exploreClass}\shell");
            if (IsOthers(shell))
            {
                continue;
            }

            using var explore = shell.CreateSubKey(exploreVerb);
            using var commandKey = explore.CreateSubKey("command");
            if (IsExplorers(explore))
            {
                Keep(commandKey, "DelegateExecute", previousHandlerName);
                Keep(commandKey, "", previousCommandName);
            }

            // One Replace adds goes off the right-click menu, as Explorer's own on Folder is and
            // this one hides. A copy of Explorer's that Replace took over stays as it was.
            if (commandKey.GetValue(previousHandlerName) == null)
            {
                explore.SetValue("ProgrammaticAccessOnly", "");
            }

            explore.SetValue(ownerName, verb);
            commandKey.SetValue("", FolderCommand);
            // The user's classes and the machine's merge value by value, and the machine's
            // explore on Folder names Explorer's handler here, which would win over the command.
            commandKey.SetValue("DelegateExecute", "");
        }

        using (var winE = classes.CreateSubKey(winEPath))
        {
            using var commandKey = winE.CreateSubKey("command");

            // Another file manager's (Files has this one), kept for Restore to put back, as a
            // displaced default verb is. Not Lode's own from a Replace before.
            if (winE.GetValue(ownerName) as string != verb)
            {
                Keep(commandKey, "", previousCommandName);
                Keep(commandKey, "DelegateExecute", previousHandlerName);
            }

            winE.SetValue(ownerName, verb);
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
            using var shell = classes.OpenSubKey($@"{folderClass}\shell", writable: true);
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

        // Every class Replace writes to is among these, so this tidies away all it left empty.
        foreach (var exploreClass in exploreClasses)
        {
            var shellPath = $@"{exploreClass}\shell";
            using (var shell = classes.OpenSubKey(shellPath, writable: true))
            {
                if (shell != null &&
                    IsLodes(shell, exploreVerb) &&
                    !GiveBackToExplorer(shell))
                {
                    shell.DeleteSubKeyTree(exploreVerb);
                }
            }

            DeleteEmptyKeys(classes, shellPath);
        }

        if (IsLodes(classes, winEPath) &&
            !GiveWinEBack(classes))
        {
            classes.DeleteSubKeyTree(winEPath);
            DeleteEmptyKeys(classes, Path.GetDirectoryName(winEPath)!);
        }

        NotifyShell();
    }

    // True when the key at path is one Replace added and marked as Lode's.
    static bool IsLodes(RegistryKey parent, string path)
    {
        using var key = parent.OpenSubKey(path);
        return key?.GetValue(ownerName) as string == verb;
    }

    // True when the explore verb under shell is there, but neither Replace's nor a copy of
    // Explorer's, so belongs to another tool.
    static bool IsOthers(RegistryKey shell)
    {
        using var explore = shell.OpenSubKey(exploreVerb);
        return explore != null &&
               explore.GetValue(ownerName) as string != verb &&
               !IsExplorers(explore);
    }

    // True when the explore verb is not Replace's and hands the folder to Explorer's own
    // handler: a copy of Explorer's in the user's classes. Through it, Chromium and Electron apps
    // open Explorer.
    static bool IsExplorers(RegistryKey explore)
    {
        if (explore.GetValue(ownerName) as string == verb)
        {
            return false;
        }

        using var commandKey = explore.OpenSubKey("command");
        return commandKey?.GetValue("DelegateExecute") is string handler &&
               handler.Equals(explorerHandler, StringComparison.OrdinalIgnoreCase);
    }

    // Copies a value, as it is and of the same kind, to where Restore finds it. Nothing when the
    // value is not there.
    static void Keep(RegistryKey key, string name, string keptName)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
        {
            key.SetValue(keptName, value, key.GetValueKind(name));
        }
    }

    // Undoes Keep: puts the value back, or deletes it when Keep found none, and drops the copy.
    static void PutBack(RegistryKey key, string name, string keptName)
    {
        if (key.GetValue(keptName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
        {
            key.SetValue(name, value, key.GetValueKind(keptName));
            key.DeleteValue(keptName);
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    // Gives a copy of Explorer's explore verb that Replace took over back to Explorer, as it
    // was. False when Replace added the verb, so it is Restore's to delete.
    static bool GiveBackToExplorer(RegistryKey shell)
    {
        using var explore = shell.OpenSubKey(exploreVerb, writable: true)!;
        using var commandKey = explore.OpenSubKey("command", writable: true);
        if (commandKey?.GetValue(previousHandlerName) == null)
        {
            return false;
        }

        PutBack(commandKey, "DelegateExecute", previousHandlerName);
        PutBack(commandKey, "", previousCommandName);
        explore.DeleteValue(ownerName);
        return true;
    }

    // Gives Win+E back to the tool Replace took it from, as it was. False when Replace added it,
    // so it is Restore's to delete.
    static bool GiveWinEBack(RegistryKey classes)
    {
        using var winE = classes.OpenSubKey(winEPath, writable: true)!;
        using var commandKey = winE.OpenSubKey("command", writable: true);
        if (commandKey == null ||
            (commandKey.GetValue(previousCommandName) == null &&
             commandKey.GetValue(previousHandlerName) == null))
        {
            return false;
        }

        PutBack(commandKey, "", previousCommandName);
        PutBack(commandKey, "DelegateExecute", previousHandlerName);
        winE.DeleteValue(ownerName);
        return true;
    }

    // Deletes the key at path, then each parent in turn, for as long as they are empty. Stops
    // short of the classes key itself.
    static void DeleteEmptyKeys(RegistryKey classes, string path)
    {
        while (path.Length > 0)
        {
            using (var key = classes.OpenSubKey(path))
            {
                if (key is {
                        SubKeyCount: > 0
                    } or
                    {
                        ValueCount: > 0
                    })
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

    // Quoted, ready for a registry command or a taskbar pin. The installed dotnet tool when
    // there is one, even when Replace is clicked in a Debug build or other copy: its shim
    // (%USERPROFILE%\.dotnet\tools\lode.exe) keeps its path across tool updates, unlike the
    // versioned copy under tools\.store, and unlike a build folder that gets cleaned or moved.
    // Only without the tool is this process registered.
    public static string PreferredCommand()
    {
        var (program, arguments) = PreferredProgram();
        if (arguments == null)
        {
            return $"\"{program}\"";
        }

        return $"\"{program}\" {arguments}";
    }

    /// <summary>
    /// What <see cref="PreferredCommand"/> starts, apart: the program, and what goes on its
    /// command line before any folder, null for nothing. For a shortcut, which keeps them apart.
    /// </summary>
    public static (string Program, string? Arguments) PreferredProgram()
    {
        if (InstalledToolPath() is { } tool)
        {
            return (tool, null);
        }

        var processPath = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started as "dotnet Lode.dll". This path is version specific, but this only
            // happens when someone starts Lode through dotnet themselves, never as the tool.
            return (processPath, $"\"{typeof(ExplorerReplacement).Assembly.Location}\"");
        }

        return (processPath, null);
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

        var path = Path.Combine(home, ".dotnet", "tools", "lode.exe");
        if (File.Exists(path))
        {
            return path;
        }

        return null;
    }
}
