/// <summary>
/// A window's place and its tabs, as it is to open again: its size before it was maximized or
/// minimized, whether it was maximized, how wide the tree is, the folder each tab shows, when
/// each was last looked at, in the same order, and which of them showed.
/// </summary>
sealed class SavedWindow
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    // At 100% scale, as Settings.TreeWidth is. 0 for the width it starts at.
    public int TreeWidth { get; set; }

    // In the pixels of the monitor the window was on, as an older Lode closing for an update
    // saved it. See Settings.SplitterDistance.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SplitterDistance { get; set; }

    public List<string> Tabs { get; set; } = [];
    public List<DateTimeOffset> TabsLastShown { get; set; } = [];
    public int ActiveTab { get; set; }
}
