/// <summary>
/// What is on the clipboard as cut, by Lode or by Explorer, which the lists show dimmed, as
/// Explorer does, until it is pasted or something else goes on the clipboard. Read again each
/// time the clipboard changes.
/// </summary>
static class CutItems
{
    static HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

    // Which change to the clipboard was last read, as Windows counts them.
    static uint read;

    // CF_HDROP.
    const uint clipboardFiles = 15;

    /// <summary>
    /// Raised, on the UI thread, when what is cut changes.
    /// </summary>
    public static event Action? Changed;

    public static bool Contains(string path) =>
        paths.Contains(path);

    /// <summary>
    /// Reads the clipboard again, for its having changed. On the UI thread, which the clipboard
    /// is read on.
    /// </summary>
    public static void Refresh()
    {
        // Every window hears of each change, and one reading of it does for them all.
        var change = GetClipboardSequenceNumber();
        if (change != 0 &&
            change == read)
        {
            return;
        }

        read = change;
        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only files can be cut, so the clipboard is opened, which waits its turn behind
        // whatever app has it, only when files are what it holds: text copied in another app
        // is most of what it hears of.
        if (IsClipboardFormatAvailable(clipboardFiles) &&
            FileOperations.ReadClipboard() is { Move: true } clipboard)
        {
            now.UnionWith(clipboard.Paths);
        }

        Set(now);
    }

    /// <summary>
    /// What is cut, as the clipboard has it. Tests set it, leaving the clipboard alone.
    /// </summary>
    internal static void Set(IEnumerable<string> cut)
    {
        var now = new HashSet<string>(cut, StringComparer.OrdinalIgnoreCase);
        if (now.SetEquals(paths))
        {
            return;
        }

        paths = now;
        Changed?.Invoke();
    }
}
