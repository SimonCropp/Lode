/// <summary>
/// A right-click menu drawn as Explorer's are: see <see cref="ExplorerRenderer"/>.
/// </summary>
sealed class ExplorerMenu : ContextMenuStrip
{
    public ExplorerMenu() =>
        ExplorerRenderer.Style(this);
}
