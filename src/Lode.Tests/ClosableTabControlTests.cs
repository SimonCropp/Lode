[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ClosableTabControlTests
{
    const int leftButtonDown = 0x0201;
    const int leftButtonUp = 0x0202;
    const int mouseMove = 0x0200;

    [Test]
    public Task DraggingATabOffTheRowHandsItOver() =>
        VerifyDrag(tab => new(tab.Left + 10, 250));

    [Test]
    public Task DraggingATabAlongTheRowKeepsIt() =>
        VerifyDrag(tab => new(tab.Right + 40, tab.Top + tab.Height / 2));

    // Less than the system's drag distance: a click that wobbled, not a drag.
    [Test]
    public Task AWobbleIsNotADrag() =>
        VerifyDrag(tab => new(tab.Left + 11, tab.Top + tab.Height / 2 + 1));

    static async Task VerifyDrag(Func<Rectangle, Point> releaseAt)
    {
        using var tabs = Create(out _);
        var draggedOff = new List<string>();
        tabs.TabDraggedOff += (page, _) => draggedOff.Add(page.Text);
        var tab = tabs.GetTabRect(1);
        var release = releaseAt(tab);

        Send(tabs, leftButtonDown, tab.Left + 10, tab.Top + tab.Height / 2);
        Send(tabs, mouseMove, release.X, release.Y);
        Send(tabs, leftButtonUp, release.X, release.Y);

        await Verify(
            new
            {
                draggedOff,
                tabs = tabs.TabPages.Cast<TabPage>().Select(_ => _.Text)
            });
    }

    static void Send(Control control, int message, int left, int top) =>
        NativeMethods.SendMessage(control.Handle, message, 1, (top << 16) | (left & 0xFFFF));

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
