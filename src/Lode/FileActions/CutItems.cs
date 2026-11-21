/// <summary>
/// What is on the clipboard as cut, by Lode or by Explorer, which the lists show dimmed, as
/// Explorer does, until it is pasted or something else goes on the clipboard. Read again each
/// time the clipboard changes.
/// </summary>
static class CutItems
{
    static HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

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
        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FileOperations.ReadClipboard() is { Move: true } clipboard)
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
