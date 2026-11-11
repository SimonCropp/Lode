[TUnit.Core.Executors.STAThreadExecutor]
public class ClosableTabControlTests
{
    const int leftButtonDown = 0x0201;

    [Test]
    public async Task ClickingACrossAsksToCloseThatTabWithoutSelectingIt()
    {
        using var tabs = Create(out var closed);
        var tab = tabs.GetTabRect(1);

        // The cross sits at the right-hand end of the tab.
        Click(tabs, tab.Right - 12, tab.Top + tab.Height / 2);

        await Verify(
            new
            {
                closed,
                selected = tabs.SelectedTab!.Text
            });
    }

    [Test]
    public async Task ClickingATabTitleSelectsIt()
    {
        using var tabs = Create(out var closed);
        var tab = tabs.GetTabRect(1);

        Click(tabs, tab.Left + 10, tab.Top + tab.Height / 2);

        await Verify(
            new
            {
                closed,
                selected = tabs.SelectedTab!.Text
            });
    }

    static ClosableTabControl Create(out List<string> closed)
    {
        var tabs = new ClosableTabControl { Size = new(600, 300) };
        tabs.TabPages.Add("First");
        tabs.TabPages.Add("Second");
        tabs.TabPages.Add("Third");
        var requests = new List<string>();
        tabs.CloseRequested += _ => requests.Add(_.Text);
        closed = requests;

        // Tabs only have positions once the control has a window.
        _ = tabs.Handle;
        return tabs;
    }

    static void Click(Control control, int left, int top) =>
        NativeMethods.SendMessage(control.Handle, leftButtonDown, 1, (top << 16) | (left & 0xFFFF));
}
