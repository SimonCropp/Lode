/// <summary>
/// What of a window a change in Options changes, so only that is done again.
/// </summary>
enum SettingsChange
{
    /// <summary>
    /// Where the actions show: the bar is made again.
    /// </summary>
    Actions,

    /// <summary>
    /// What a folder lists, hidden or system items: every folder is read again, and the tree,
    /// which lists folders too, built again.
    /// </summary>
    Listing,

    /// <summary>
    /// Whether Recent shows: the tree's Pinned and Recent sections are made again.
    /// </summary>
    Places,

    /// <summary>
    /// Whether the tree expands to the open folder: it is selected again.
    /// </summary>
    TreeSelection
}
