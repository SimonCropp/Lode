using System.Collections.Concurrent;

/// <summary>
/// Opens a new GitHub issue on Lode's repository in the browser, with its title and body filled
/// in: from About, blank but for the version and OS, and after an error nothing caught, offered
/// with what went wrong.
/// </summary>
static class IssueLauncher
{
    const string repository = "https://github.com/SimonCropp/Lode";

    static readonly ConcurrentDictionary<string, byte> raised = new();

    static string DefaultBody =>
        $"""
          * Lode Version: {Updates.Instance.Current.ToNormalizedString()}
          * OS: {Environment.OSVersion.VersionString}
         """;

    public static void Launch() =>
        OpenUrl(BuildUrl(""));

    /// <summary>
    /// The title is encoded as the body is. One built from a message carrying a file path had a
    /// '#' in it, which started a fragment and dropped the body and the rest of the title, or an
    /// '&amp;', which started a parameter GitHub does not have and cut the title short there.
    /// <paramref name="extraBody"/> is plain text, after the version and OS.
    /// </summary>
    internal static string BuildUrl(string title, string extraBody = "") =>
        $"{repository}/issues/new?title={WebUtility.UrlEncode(title)}&body={WebUtility.UrlEncode(DefaultBody + extraBody)}";

    /// <summary>
    /// Whether <paramref name="message"/> is being raised for the first time this run, and so
    /// whether to offer an issue for it at all: a failure that repeats, every paint or every
    /// tick, otherwise asked every time.
    /// </summary>
    internal static bool FirstTime(string message) =>
        raised.TryAdd(message, 0);

    public static void LaunchForException(string message, Exception exception)
    {
        if (!FirstTime(message))
        {
            return;
        }

        var text = $"""
                    An error occurred: {message}

                    {exception.GetType().Name}: {exception.Message}

                    Open an issue on GitHub?
                    """;
        if (Declined(text))
        {
            return;
        }

        var extraBody =
            $"""

              * Action: {message}
              * Exception:
             ```
             {exception}
             ```
             """;
        OpenUrl(BuildUrl(message, extraBody));
    }

    /// <summary>
    /// Puts the question, and answers whether it was declined. A field so that a test can decline
    /// without anybody being asked: the box is modal, and a test run that reached it would wait
    /// there for a click.
    /// </summary>
    internal static Func<string, bool> Declined = text =>
        MessageBox.Show(
            text,
            "Lode Error",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Error) == DialogResult.No;

    /// <summary>
    /// Never thrown: no browser registered, or one that will not start, is nothing Lode can do
    /// anything about, and thrown from a click, or from reporting an error, it reached the UI
    /// thread in place of whatever was being reported.
    /// </summary>
    public static void OpenUrl(string url)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
        }
        catch (Exception)
        {
        }
    }
}
