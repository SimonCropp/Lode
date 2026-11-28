/// <summary>
/// Where a file's changes show when picked in the Changes or History pane.
/// </summary>
enum DiffPlacement
{
    // A tab of their own beside the folders' tabs, the panes staying beside it.
    Tab,

    // A window of their own, with the files changed down its left.
    Window,

    // Over the folder list of the tab showing, till the file is no longer picked.
    Folder,

    // The text editor, VS Code say, or git's diff tool.
    Editor
}