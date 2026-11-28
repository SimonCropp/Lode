/// <summary>
/// The tabs closed while Lode runs, in any window, for Reopen closed tab (Ctrl+Shift+T) and the
/// Recently closed submenu to bring back in whichever window they are asked in: a tab closed,
/// one closed as unused, and those of a window closed, its last tab say. One list for every
/// window, as it is kept with the settings they share (see <see cref="Settings.ClosedTabs"/>),
/// and not saved: Lode keeps running in the tray, and a start opens the tabs that were open
/// rather than those closed before.
/// </summary>
sealed class ClosedTabs
{
    const int maximumKept = 20;

    // Most recent last.
    List<ClosedTab> tabs = [];

    public void Add(string folder)
    {
        tabs.Add(new(folder));
        if (tabs.Count > maximumKept)
        {
            tabs.RemoveAt(0);
        }
    }

    public bool Any => tabs.Count > 0;

    /// <summary>
    /// The tabs kept, the most recently closed first, as the submenu lists them.
    /// </summary>
    public List<ClosedTab> NewestFirst() =>
        tabs.AsEnumerable().Reverse().ToList();

    /// <summary>
    /// The most recently closed, no longer kept, or null with none.
    /// </summary>
    public ClosedTab? TakeLast()
    {
        if (tabs.Count == 0)
        {
            return null;
        }

        var last = tabs[^1];
        tabs.RemoveAt(tabs.Count - 1);
        return last;
    }

    /// <summary>
    /// <paramref name="tab"/>, reopened from the submenu, no longer kept.
    /// </summary>
    public void Take(ClosedTab tab) =>
        tabs.Remove(tab);
}

/// <summary>
/// A tab closed, by the folder it showed. Each closing is one of its own, a folder closed twice
/// being two, so one reopened from the submenu is the one picked.
/// </summary>
sealed class ClosedTab(string folder)
{
    public string Folder => folder;
}
