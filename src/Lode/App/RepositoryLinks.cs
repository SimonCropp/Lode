/// <summary>
/// Makes Lode what opens the links code sites give to hand a repository to a desktop app:
/// GitHub's Open with GitHub Desktop, and the Open with Visual Studio of GitHub and Clone in
/// Visual Studio of Azure DevOps. Lode opens the repository from the code folder instead (see
/// <see cref="RepositoryLink"/>), and hands a link for one not there to the app it was for.
/// </summary>
/// <remarks>
/// Writes only to the current user's registry, as <see cref="ExplorerReplacement"/> does. Each
/// kind of link is taken over, and given back, on its own.
/// <para>
/// Take points the scheme's own command at Lode, and also registers Lode as an app that can
/// open the links, so Windows' Default apps offers it for them. An app that writes its command
/// back over Lode's, as GitHub Desktop does each time it starts, takes the links back, unless
/// Lode is chosen for them in Default apps, which Windows asks ahead of the scheme's command.
/// Only the user can make that choice: Windows keeps it with a hash only Settings writes.
/// </para>
/// </remarks>
sealed class RepositoryLinks(
    string classesPath,
    string command,
    string associationsPath = RepositoryLinks.userAssociationsPath,
    Func<string, int, string?>? association = null,
    bool notifyShell = false)
{
    /// <summary>
    /// A kind of link: the scheme it has, what the site's button says, the app it is for, and
    /// whether that app takes the links back when it starts.
    /// </summary>
    public sealed record Handler(string Id, string Scheme, string Buttons, string App, bool TakesBack);

    public static readonly Handler GitHubDesktop = new(
        "gitHubDesktop",
        "x-github-client",
        "GitHub's Open with GitHub Desktop",
        "GitHub Desktop",
        TakesBack: true);

    public static readonly Handler VisualStudio = new(
        "visualStudio",
        "git-client",
        "GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio",
        "Visual Studio",
        TakesBack: false);

    /// <summary>
    /// The name Lode registers under for Windows' Default apps.
    /// </summary>
    public const string RegisteredName = "Lode";

    /// <summary>
    /// The Settings page of the apps Lode can be chosen for.
    /// </summary>
    public const string DefaultAppsPage = $"ms-settings:defaultapps?registeredAppUser={RegisteredName}";

    // What Default apps sets as the user's choice when Lode is picked: its command is Lode's.
    const string progId = "Lode.RepositoryLink";

    public static readonly IReadOnlyList<Handler> All = [GitHubDesktop, VisualStudio];

    const string owner = "Lode";

    // On the scheme's key while Lode has it, or had it until the app took the links back.
    const string takenName = "TakenBy";

    // On the command key: the command Take wrote. An app that takes its links back, as GitHub
    // Desktop does each time it starts, writes its own command over Lode's but leaves the rest,
    // so the links are Lode's only while the command is still this one.
    const string lodeCommandName = "LodeCommand";

    // On the scheme's key when Take made it, so GiveBack deletes it all.
    const string ownerName = "CreatedBy";

    // On the command key: what it had before, for GiveBack to put back, and the command Windows
    // ran for the links before, for a link Lode can't open to be handed on to.
    const string previousCommandName = "LodePreviousCommand";
    const string forwardCommandName = "LodeForwardCommand";

    const string userAssociationsPath = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations";

    // What Windows has for links with a scheme: AssocFriendlyAppName or AssocCommand. Tests swap
    // it, as they must not depend on the apps the machine has.
    Func<string, int, string?> association = association ?? Association;

    public static RepositoryLinks ForCurrentUser() =>
        new(@"Software\Classes", ExplorerReplacement.PreferredCommand(), notifyShell: true);

    /// <summary>
    /// For the same classes and copy of Lode as <paramref name="replacement"/>.
    /// </summary>
    public static RepositoryLinks Like(ExplorerReplacement replacement) =>
        new(replacement.ClassesPath, replacement.Command, notifyShell: replacement.NotifiesShell);

    string LinkCommand => command + " \"%1\"";

    string SchemePath(Handler handler) => $@"{classesPath}\{handler.Scheme}";

    string CommandPath(Handler handler) => $@"{SchemePath(handler)}\shell\open\command";

    string ProgIdPath => $@"{classesPath}\{progId}";

    // The key the classes are under, Software for the real ones: Lode's registration for Default
    // apps goes beside them.
    string SoftwarePath => Path.GetDirectoryName(classesPath) ?? "";

    string CapabilitiesPath => $@"{SoftwarePath}\{RegisteredName}\Capabilities";

    string RegisteredApplicationsPath => $@"{SoftwarePath}\RegisteredApplications";

    string ChoicePath(Handler handler) => $@"{associationsPath}\{handler.Scheme}\UserChoice";

    /// <summary>
    /// True when the links open in a Lode, this copy or another: the scheme's command is Lode's,
    /// or Lode is chosen for them in Windows' Default apps.
    /// </summary>
    public bool IsTaken(Handler handler) =>
        IsChosen(handler) || IsInClasses(handler);

    /// <summary>
    /// True when Lode is chosen for the links in Windows' Default apps, so they open in Lode
    /// whatever the scheme's own command is.
    /// </summary>
    public bool IsChosen(Handler handler) =>
        ChosenInWindows(handler) == progId &&
        RegisteredCommand() != null;

    // True when the scheme's own command is the one Take wrote.
    bool IsInClasses(Handler handler)
    {
        if (!IsMarked(handler))
        {
            return false;
        }

        using var commandKey = Registry.CurrentUser.OpenSubKey(CommandPath(handler));
        if (commandKey?.GetValue("") is not string current)
        {
            return false;
        }

        // A Take from before Lode noted the command it wrote: only this copy's is known.
        var written = commandKey.GetValue(lodeCommandName) as string ?? LinkCommand;
        return current == written;
    }

    /// <summary>
    /// True when Lode took the links and the app they were for has since taken them back.
    /// </summary>
    public bool IsTakenBack(Handler handler) =>
        IsMarked(handler) && !IsTaken(handler);

    bool IsMarked(Handler handler)
    {
        using var scheme = Registry.CurrentUser.OpenSubKey(SchemePath(handler));
        return scheme?.GetValue(takenName) as string == owner;
    }

    /// <summary>
    /// True when the links open in the copy of Lode Take registers.
    /// </summary>
    public bool IsTakenHere(Handler handler)
    {
        if (IsChosen(handler))
        {
            return RegisteredCommand() == LinkCommand;
        }

        if (!IsInClasses(handler))
        {
            return false;
        }

        return SchemeCommand(handler) == LinkCommand;
    }

    /// <summary>
    /// True when Lode is registered for the links, so Windows' Default apps offers it for them.
    /// </summary>
    public bool IsRegistered(Handler handler)
    {
        using var associations = Registry.CurrentUser.OpenSubKey($@"{CapabilitiesPath}\URLAssociations");
        return associations?.GetValue(handler.Scheme) as string == progId;
    }

    /// <summary>
    /// The app the user picked for the links in Windows' own settings, which Windows asks for
    /// them in place of the scheme's command. Null when there is none.
    /// </summary>
    public string? ChosenInWindows(Handler handler)
    {
        using var choice = Registry.CurrentUser.OpenSubKey(ChoicePath(handler));
        return choice?.GetValue("ProgId") as string;
    }

    /// <summary>
    /// Whether an app other than Lode is chosen for the links in Windows' Default apps, which
    /// comes ahead of the command Take writes, and which.
    /// </summary>
    public string? OtherChosenInWindows(Handler handler)
    {
        var chosen = ChosenInWindows(handler);
        if (chosen == progId)
        {
            return null;
        }

        return chosen;
    }

    /// <summary>
    /// The name of the app other than Lode chosen for the links in Windows' Default apps, as its
    /// registration gives it, else as Windows names the ProgId's program, else the ProgId. Null
    /// when no other app is chosen.
    /// </summary>
    public string? OtherChosenAppName(Handler handler)
    {
        if (OtherChosenInWindows(handler) is not { } chosen)
        {
            return null;
        }

        return RegisteredAppName(handler.Scheme, chosen) ?? ProgIdAppName(chosen) ?? chosen;
    }

    // True for the real classes. The machine's registrations, and what Windows says of a
    // ProgId, go with them: tests' scratch classes are never mixed with this machine's.
    bool IsRealClasses => SoftwarePath.Equals("Software", StringComparison.OrdinalIgnoreCase);

    // The name of the app registered for Default apps with chosen as its ProgId for the scheme.
    // An app's registration can be in the user's registry or the machine's, and in the machine's
    // the list and what it points at can be in either view: Visual Studio lists itself in the
    // 64-bit view and keeps its capabilities in the 32-bit one.
    string? RegisteredAppName(string scheme, string chosen)
    {
        var name = RegisteredAppName([Registry.CurrentUser], SoftwarePath, scheme, chosen);
        if (name != null ||
            !IsRealClasses)
        {
            return name;
        }

        using var machine64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        return RegisteredAppName([machine64, machine32], "Software", scheme, chosen);
    }

    static string? RegisteredAppName(RegistryKey[] views, string software, string scheme, string chosen)
    {
        foreach (var view in views)
        {
            using var applications = view.OpenSubKey($@"{software}\RegisteredApplications");
            if (applications == null)
            {
                continue;
            }

            foreach (var registered in applications.GetValueNames())
            {
                if (applications.GetValue(registered) is not string path)
                {
                    continue;
                }

                foreach (var capabilitiesView in views)
                {
                    using var capabilities = capabilitiesView.OpenSubKey(path);
                    using var associations = capabilities?.OpenSubKey("URLAssociations");
                    if (associations?.GetValue(scheme) as string != chosen)
                    {
                        continue;
                    }

                    return Readable(capabilities!.GetValue("ApplicationName") as string) ?? registered;
                }
            }
        }

        return null;
    }

    // A name as a registration gives it, with "@file.dll,-id" read from the file. Null for none,
    // or one that can't be read.
    static string? Readable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (!name.StartsWith('@'))
        {
            return name;
        }

        var buffer = new char[512];
        if (SHLoadIndirectString(name, buffer, (uint) buffer.Length, IntPtr.Zero) < 0)
        {
            return null;
        }

        var end = Array.IndexOf(buffer, '\0');
        if (end < 0)
        {
            end = buffer.Length;
        }

        var text = new string(buffer, 0, end);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text;
    }

    // What Windows names the program of the ProgId, for an app that registers no name, such as
    // Visual Studio's Build Tools.
    string? ProgIdAppName(string chosen)
    {
        if (!IsRealClasses)
        {
            return null;
        }

        return Query(0, chosen, AssocFriendlyAppName);
    }

    string? SchemeCommand(Handler handler)
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey(CommandPath(handler));
        return commandKey?.GetValue("") as string;
    }

    // The command of the ProgId Default apps chooses, null when Lode isn't registered.
    string? RegisteredCommand()
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey($@"{ProgIdPath}\shell\open\command");
        return commandKey?.GetValue("") as string;
    }

    /// <summary>
    /// The name of the app Windows opens the links in, null for none.
    /// </summary>
    public string? CurrentApp(Handler handler) =>
        association(handler.Scheme, AssocFriendlyAppName);

    public void Take(Handler handler)
    {
        // Asked before anything changes, so it is the app the links went to, not Lode. Links the
        // app took back are kept again, as its command now may be another than the one kept,
        // such as GitHub Desktop's, which names the folder of the version that wrote it.
        // With Lode chosen in Default apps, Windows names Lode for the links, so the app they go
        // to otherwise is the one the scheme's command names.
        var inClasses = IsInClasses(handler);
        string? forward = null;
        if (!inClasses)
        {
            forward = IsChosen(handler) ? SchemeCommand(handler) : association(handler.Scheme, AssocCommand);
        }

        using (var scheme = Registry.CurrentUser.OpenSubKey(SchemePath(handler), writable: true) ??
                            Created(handler))
        {
            TakeScheme(scheme, inClasses, forward);
        }

        Register(handler);
        NotifyShell();
    }

    void TakeScheme(RegistryKey scheme, bool inClasses, string? forward)
    {
        using var commandKey = scheme.CreateSubKey(@"shell\open\command");
        if (!inClasses)
        {
            Keep(commandKey, "", previousCommandName);
            if (forward == null)
            {
                commandKey.DeleteValue(forwardCommandName, throwOnMissingValue: false);
            }
            else
            {
                commandKey.SetValue(forwardCommandName, forward);
            }

            // An app wrote its command into a scheme key Take made, so it is the app's now:
            // GiveBack puts that command back rather than deleting the key.
            if (commandKey.GetValue(previousCommandName) != null)
            {
                scheme.DeleteValue(ownerName, throwOnMissingValue: false);
            }
        }

        // A scheme key without it is not a link Windows will hand on.
        scheme.SetValue("URL Protocol", "");
        scheme.SetValue(takenName, owner);
        commandKey.SetValue("", LinkCommand);
        commandKey.SetValue(lodeCommandName, LinkCommand);
    }

    RegistryKey Created(Handler handler)
    {
        var scheme = Registry.CurrentUser.CreateSubKey(SchemePath(handler));
        scheme.SetValue("", $"URL:{handler.Scheme}");
        scheme.SetValue(ownerName, owner);
        return scheme;
    }

    // Registers Lode as an app that can open the links, for Windows' Default apps to offer: a
    // ProgId with Lode's command, the schemes it can open, and the registration naming them.
    void Register(Handler handler)
    {
        using (var registered = Registry.CurrentUser.CreateSubKey(ProgIdPath))
        {
            registered.SetValue("", "Lode repository link");
            using var commandKey = registered.CreateSubKey(@"shell\open\command");
            commandKey.SetValue("", LinkCommand);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", RegisteredName);
            capabilities.SetValue(
                "ApplicationDescription",
                "Opens repositories from your code folder when a code site's button would hand them to a desktop app.");
            if (Icon() is { } icon)
            {
                capabilities.SetValue("ApplicationIcon", icon);
            }

            using var associations = capabilities.CreateSubKey("URLAssociations");
            associations.SetValue(handler.Scheme, progId);
        }

        using var applications = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath);
        applications.SetValue(RegisteredName, CapabilitiesPath);
    }

    // Lode's program, for Default apps to show its icon: the first part of the command, when that
    // is Lode itself and not dotnet running it.
    string? Icon()
    {
        if (!command.StartsWith('"'))
        {
            return null;
        }

        var end = command.IndexOf('"', 1);
        if (end < 0)
        {
            return null;
        }

        var program = command[1..end];
        if (!Path.GetFileName(program).Equals("lode.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"\"{program}\",0";
    }

    // Undoes Register for the links, and all of it once Lode is registered for none.
    void Unregister(Handler handler)
    {
        using (var associations = Registry.CurrentUser.OpenSubKey($@"{CapabilitiesPath}\URLAssociations", writable: true))
        {
            if (associations == null)
            {
                return;
            }

            if (associations.GetValue(handler.Scheme) as string == progId)
            {
                associations.DeleteValue(handler.Scheme);
            }

            if (associations.ValueCount > 0)
            {
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
        DeleteIfEmpty($@"{SoftwarePath}\{RegisteredName}");
        Registry.CurrentUser.DeleteSubKeyTree(ProgIdPath, throwOnMissingSubKey: false);
        using (var applications = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: true))
        {
            if (applications?.GetValue(RegisteredName) as string == CapabilitiesPath)
            {
                applications.DeleteValue(RegisteredName);
            }
        }

        DeleteIfEmpty(RegisteredApplicationsPath);
    }

    // Takes away the user's choice of Lode in Default apps, so Windows goes back to the scheme's
    // command. Settings writes the choice, with a hash, but a key the user can delete. Left when
    // Windows won't let it go: Default apps still names Lode then, and Options says so.
    void ForgetChoice(Handler handler)
    {
        foreach (var path in new[] { ChoicePath(handler), ChoicePath(handler) + "Latest" })
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
            catch (Exception exception)
                when (exception is
                    UnauthorizedAccessException or
                    SecurityException or
                    IOException)
            {
            }
        }

        DeleteIfEmpty($@"{associationsPath}\{handler.Scheme}");
    }

    static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is not
                {
                    SubKeyCount: 0,
                    ValueCount: 0
                })
            {
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    // Tells Explorer the associations changed, so Default apps and the links see the change.
    void NotifyShell()
    {
        if (notifyShell)
        {
            SHChangeNotify(ShellChangeAssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void GiveBack(Handler handler)
    {
        var chosen = IsChosen(handler);
        GiveBackScheme(handler);
        Unregister(handler);
        if (chosen)
        {
            ForgetChoice(handler);
        }

        NotifyShell();
    }

    void GiveBackScheme(Handler handler)
    {
        var schemePath = SchemePath(handler);
        var takenBack = IsMarked(handler) && !IsInClasses(handler);
        using (var scheme = Registry.CurrentUser.OpenSubKey(schemePath, writable: true))
        {
            if (scheme?.GetValue(takenName) as string != owner)
            {
                return;
            }

            // The app has its links already, with a command newer than the one kept: only what
            // Lode left goes.
            if (takenBack)
            {
                using (var commandKey = scheme.OpenSubKey(@"shell\open\command", writable: true))
                {
                    if (commandKey != null)
                    {
                        commandKey.DeleteValue(previousCommandName, throwOnMissingValue: false);
                        commandKey.DeleteValue(forwardCommandName, throwOnMissingValue: false);
                        commandKey.DeleteValue(lodeCommandName, throwOnMissingValue: false);
                    }
                }

                scheme.DeleteValue(ownerName, throwOnMissingValue: false);
                scheme.DeleteValue(takenName);
                return;
            }

            if (scheme.GetValue(ownerName) as string == owner)
            {
                scheme.Close();
                Registry.CurrentUser.DeleteSubKeyTree(schemePath, throwOnMissingSubKey: false);
                return;
            }

            using (var commandKey = scheme.OpenSubKey(@"shell\open\command", writable: true))
            {
                if (commandKey != null)
                {
                    PutBack(commandKey, "", previousCommandName);
                    commandKey.DeleteValue(forwardCommandName, throwOnMissingValue: false);
                    commandKey.DeleteValue(lodeCommandName, throwOnMissingValue: false);
                }
            }

            scheme.DeleteValue(takenName);
        }

        DeleteEmptyKeys(schemePath, @"shell\open\command");
    }

    /// <summary>
    /// The command a link Lode can't open is handed on with: the one Windows ran for the links
    /// before Take, and, before Take or after GiveBack, the one it runs now. With Lode chosen in
    /// Default apps and the scheme's command written back by the app, that command. Null for none.
    /// </summary>
    public string? ForwardCommand(Handler handler)
    {
        if (!IsTaken(handler))
        {
            return association(handler.Scheme, AssocCommand);
        }

        if (!IsInClasses(handler) &&
            SchemeCommand(handler) is { } scheme)
        {
            return scheme;
        }

        using var commandKey = Registry.CurrentUser.OpenSubKey(CommandPath(handler));
        return commandKey?.GetValue(forwardCommandName) as string;
    }

    /// <summary>
    /// Whether <paramref name="argument"/> is a link of one of <see cref="All"/>, and which.
    /// </summary>
    public static Handler? HandlerOf(string argument) =>
        All.FirstOrDefault(_ => argument.StartsWith(_.Scheme + ":", StringComparison.OrdinalIgnoreCase));

    // Copies a value to where GiveBack finds it. Nothing when the value is not there.
    static void Keep(RegistryKey key, string name, string keptName)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
        {
            key.SetValue(keptName, value, key.GetValueKind(name));
        }
        else
        {
            key.DeleteValue(keptName, throwOnMissingValue: false);
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

    // Deletes the keys of path under the scheme's key, the deepest first, for as long as they
    // are empty. The scheme's key stays: it was there before Take.
    static void DeleteEmptyKeys(string schemePath, string path)
    {
        while (path.Length > 0)
        {
            var fullPath = $@"{schemePath}\{path}";
            using (var key = Registry.CurrentUser.OpenSubKey(fullPath))
            {
                if (key is
                    {
                        SubKeyCount: > 0
                    }
                    or
                    {
                        ValueCount: > 0
                    })
                {
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKey(fullPath, throwOnMissingSubKey: false);
            path = Path.GetDirectoryName(path) ?? "";
        }
    }

    // What Windows has for links with the scheme, as AssocQueryString says, or null.
    static string? Association(string scheme, int what) =>
        Query(AssocIsProtocol, scheme, what);

    // What AssocQueryString says of a scheme or ProgId, or null.
    static string? Query(int flags, string association, int what)
    {
        var length = 0;
        if (AssocQueryString(flags, what, association, null, IntPtr.Zero, ref length) < 0 ||
            length == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length * sizeof(char));
        try
        {
            if (AssocQueryString(flags, what, association, null, buffer, ref length) < 0)
            {
                return null;
            }

            var text = Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
