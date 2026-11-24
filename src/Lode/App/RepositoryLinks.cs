/// <summary>
/// Makes Lode what opens the links code sites give to hand a repository to a desktop app:
/// GitHub's Open with GitHub Desktop, and the Open with Visual Studio of GitHub and Clone in
/// Visual Studio of Azure DevOps. Lode opens the repository from the code folder instead (see
/// <see cref="RepositoryLink"/>), and hands a link for one not there to the app it was for.
/// </summary>
/// <remarks>
/// Writes only to the current user's classes, as <see cref="ExplorerReplacement"/> does. Each
/// kind of link is taken over, and given back, on its own.
/// </remarks>
sealed class RepositoryLinks(
    string classesPath,
    string command,
    string associationsPath = RepositoryLinks.userAssociationsPath,
    Func<string, int, string?>? association = null)
{
    /// <summary>
    /// A kind of link: the scheme it has, what the site's button says, and the app it is for.
    /// </summary>
    public sealed record Handler(string Id, string Scheme, string Buttons, string App);

    public static readonly Handler GitHubDesktop = new(
        "gitHubDesktop",
        "x-github-client",
        "GitHub's Open with GitHub Desktop",
        "GitHub Desktop");

    public static readonly Handler VisualStudio = new(
        "visualStudio",
        "git-client",
        "GitHub's Open with Visual Studio and Azure DevOps' Clone in Visual Studio",
        "Visual Studio");

    public static readonly IReadOnlyList<Handler> All = [GitHubDesktop, VisualStudio];

    const string owner = "Lode";

    // On the scheme's key while Lode has it.
    const string takenName = "TakenBy";

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
        new(@"Software\Classes", ExplorerReplacement.PreferredCommand());

    /// <summary>
    /// For the same classes and copy of Lode as <paramref name="replacement"/>.
    /// </summary>
    public static RepositoryLinks Like(ExplorerReplacement replacement) =>
        new(replacement.ClassesPath, replacement.Command);

    string LinkCommand => command + " \"%1\"";

    string SchemePath(Handler handler) => $@"{classesPath}\{handler.Scheme}";

    string CommandPath(Handler handler) => $@"{SchemePath(handler)}\shell\open\command";

    /// <summary>
    /// True when the links open in a Lode, this copy or another.
    /// </summary>
    public bool IsTaken(Handler handler)
    {
        using var scheme = Registry.CurrentUser.OpenSubKey(SchemePath(handler));
        return scheme?.GetValue(takenName) as string == owner;
    }

    /// <summary>
    /// True when the links open in the copy of Lode Take registers.
    /// </summary>
    public bool IsTakenHere(Handler handler)
    {
        if (!IsTaken(handler))
        {
            return false;
        }

        using var commandKey = Registry.CurrentUser.OpenSubKey(CommandPath(handler));
        return commandKey?.GetValue("") as string == LinkCommand;
    }

    /// <summary>
    /// The app the user picked for the links in Windows' own settings, which Windows asks for
    /// them in place of the command Take writes. Null when there is none.
    /// </summary>
    public string? ChosenInWindows(Handler handler)
    {
        using var choice = Registry.CurrentUser.OpenSubKey($@"{associationsPath}\{handler.Scheme}\UserChoice");
        return choice?.GetValue("ProgId") as string;
    }

    /// <summary>
    /// The name of the app Windows opens the links in, null for none.
    /// </summary>
    public string? CurrentApp(Handler handler) =>
        association(handler.Scheme, AssocFriendlyAppName);

    public void Take(Handler handler)
    {
        // Asked before anything changes, so it is the app the links went to, not Lode.
        var forward = IsTaken(handler) ? null : association(handler.Scheme, AssocCommand);

        using var scheme = Registry.CurrentUser.OpenSubKey(SchemePath(handler), writable: true) ??
                           Created(handler);
        using var commandKey = scheme.CreateSubKey(@"shell\open\command");
        if (scheme.GetValue(takenName) as string != owner)
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
        }

        // A scheme key without it is not a link Windows will hand on.
        scheme.SetValue("URL Protocol", "");
        scheme.SetValue(takenName, owner);
        commandKey.SetValue("", LinkCommand);
    }

    RegistryKey Created(Handler handler)
    {
        var scheme = Registry.CurrentUser.CreateSubKey(SchemePath(handler));
        scheme.SetValue("", $"URL:{handler.Scheme}");
        scheme.SetValue(ownerName, owner);
        return scheme;
    }

    public void GiveBack(Handler handler)
    {
        var schemePath = SchemePath(handler);
        using (var scheme = Registry.CurrentUser.OpenSubKey(schemePath, writable: true))
        {
            if (scheme?.GetValue(takenName) as string != owner)
            {
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
                }
            }

            scheme.DeleteValue(takenName);
        }

        DeleteEmptyKeys(schemePath, @"shell\open\command");
    }

    /// <summary>
    /// The command a link Lode can't open is handed on with: the one Windows ran for the links
    /// before Take, and, before Take or after GiveBack, the one it runs now. Null for none.
    /// </summary>
    public string? ForwardCommand(Handler handler)
    {
        if (!IsTaken(handler))
        {
            return association(handler.Scheme, AssocCommand);
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
    static string? Association(string scheme, int what)
    {
        var length = 0;
        if (AssocQueryString(AssocIsProtocol, what, scheme, null, IntPtr.Zero, ref length) < 0 ||
            length == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length * sizeof(char));
        try
        {
            if (AssocQueryString(AssocIsProtocol, what, scheme, null, buffer, ref length) < 0)
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
