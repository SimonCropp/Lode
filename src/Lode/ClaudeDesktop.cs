/// <summary>
/// Opens a folder in a new Claude Code session of the Claude desktop app, through the link the
/// app's own ccd command opens for a folder. Without the app, Open in Claude is not offered.
/// </summary>
static class ClaudeDesktop
{
    /// <summary>
    /// Whether the Claude desktop app is installed: whether Windows has an app for claude: links.
    /// </summary>
    internal static Func<bool> IsInstalled { get; set; } = Installed;

    // Asking Windows takes a fraction of a millisecond, and the actions bar asks each time the
    // selection changes, so an answer stands for a few seconds. Installing or removing the app
    // while Lode runs shows in the menus soon after.
    const long answerLifetime = 5000;
    static (long At, bool Installed)? answer;

    static bool Installed()
    {
        var now = Environment.TickCount64;
        if (answer is { } known &&
            now - known.At < answerLifetime)
        {
            return known.Installed;
        }

        var installed = HasApp("claude");
        answer = (now, installed);
        return installed;
    }

    /// <summary>
    /// Whether Windows has an app for links with <paramref name="scheme"/>. Asks for the app's
    /// name, which a packaged app such as Claude's has, where it has no command or executable.
    /// </summary>
    internal static bool HasApp(string scheme)
    {
        var length = 0;
        return AssocQueryString(AssocIsProtocol, AssocFriendlyAppName, scheme, null, IntPtr.Zero, ref length) >= 0;
    }

    /// <summary>
    /// Open in Claude for <paramref name="folders"/>, or nothing without the desktop app or a
    /// folder. A session works in one folder, so it is greyed out for several.
    /// </summary>
    public static List<QuickMenu.Command?> Commands(IWin32Window owner, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0 ||
            !IsInstalled())
        {
            return [];
        }

        return [LodeActions.Command("claude", () => Open(owner, folders[0]), folders.Count == 1)];
    }

    /// <summary>
    /// The link that opens a new session in <paramref name="folder"/>.
    /// </summary>
    internal static string Link(string folder) =>
        $"claude://code/new?folder={Uri.EscapeDataString(folder)}";

    public static void Open(IWin32Window owner, string folder)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Link(folder))
            {
                UseShellExecute = true
            };
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception)
        {
            // The app was removed since the menu was shown, say.
            MessageBox.Show(owner, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
