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

    // Over the tab after it, it goes after that tab, and is handed nowhere.
    [Test]
    public Task DraggingATabAlongTheRowMovesIt() =>
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

    // A tab before the one dragged, closed by Ctrl+W meanwhile, leaves the drag with the tab it
    // started on. One closed itself hands nothing over.
    [Test]
    public async Task ADragKeepsItsTabWhenAnotherCloses()
    {
        using var tabs = Create(out _);
        var draggedOff = new List<string>();
        tabs.TabDraggedOff += (page, _) => draggedOff.Add(page.Text);

        var second = tabs.GetTabRect(1);
        Send(tabs, leftButtonDown, second.Left + 10, second.Top + second.Height / 2);
        Send(tabs, mouseMove, second.Left + 10, 250);
        tabs.TabPages.RemoveAt(0);
        Send(tabs, leftButtonUp, second.Left + 10, 250);

        var third = tabs.GetTabRect(1);
        Send(tabs, leftButtonDown, third.Left + 10, third.Top + third.Height / 2);
        Send(tabs, mouseMove, third.Left + 10, 250);
        tabs.TabPages.RemoveAt(1);
        Send(tabs, leftButtonUp, third.Left + 10, 250);

        await Assert.That(draggedOff).IsEquivalentTo(["Second"]);
    }

    // Dragged along the row, the tab moves past each it crosses, right to the start, beyond the
    // first. It stays the tab selected all the while, with nothing saying the selection changed,
    // which would have the window show another tab, and every tab keeps its icon.
    [Test]
    public async Task DraggingATabAlongTheRowKeepsItSelected()
    {
        using var tabs = Create(out _);
        for (var index = 0; index < tabs.TabCount; index++)
        {
            tabs.SetIcon(tabs.TabPages[index], index + 1);
        }

        var third = tabs.GetTabRect(2);
        var middle = third.Top + third.Height / 2;
        Send(tabs, leftButtonDown, third.Left + 10, middle);
        var selectionChanges = 0;
        tabs.SelectedIndexChanged += (_, _) => selectionChanges++;
        tabs.Selecting += (_, _) => selectionChanges++;

        Send(tabs, mouseMove, tabs.GetTabRect(1).Left + 10, middle);
        var past = Texts(tabs);
        Send(tabs, mouseMove, 2, middle);
        Send(tabs, leftButtonUp, 2, middle);

        await Assert.That(past).IsEquivalentTo(["First", "Third", "Second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Texts(tabs)).IsEquivalentTo(["Third", "First", "Second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(tabs.SelectedTab!.Text).IsEqualTo("Third");
        await Assert.That(selectionChanges).IsEqualTo(0);
        await Assert.That(tabs.TabPages.Cast<TabPage>().Select(tabs.IconOf)).IsEquivalentTo(new int?[] { 3, 1, 2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Escape gives up on the drag, and the tab goes back where it was.
    [Test]
    public async Task EscapePutsTheTabBack()
    {
        const int keyDown = 0x0100;
        const int escape = 0x1B;
        using var tabs = Create(out _);
        var first = tabs.GetTabRect(0);
        var middle = first.Top + first.Height / 2;
        Send(tabs, leftButtonDown, first.Left + 10, middle);
        Send(tabs, mouseMove, tabs.GetTabRect(2).Left + 10, middle);
        var dragged = Texts(tabs);

        NativeMethods.SendMessage(tabs.Handle, keyDown, escape, IntPtr.Zero);
        Send(tabs, leftButtonUp, tabs.GetTabRect(2).Left + 10, middle);

        await Assert.That(dragged).IsEquivalentTo(["Second", "Third", "First"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Texts(tabs)).IsEquivalentTo(["First", "Second", "Third"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(tabs.SelectedTab!.Text).IsEqualTo("First");
    }

    // Painted while the selected tab, the last, is being taken out, dragged to another window
    // say: Windows Forms counts it gone before the window is told, so the window still has it
    // selected, past the tabs counted. Here the paint comes just as the window is told.
    [Test]
    public async Task PaintingWhileTheSelectedTabGoes()
    {
        using var tabs = Create(out _);
        tabs.SelectedIndex = 2;
        using var spy = new PaintOnDelete(tabs);

        tabs.TabPages.RemoveAt(2);

        await Assert.That(spy.Thrown).IsNull();
        await Assert.That(spy.Painted).IsTrue();
    }

    // Paints the tabs as the window is told to delete one, before it does.
    sealed class PaintOnDelete : NativeWindow, IDisposable
    {
        const int deleteItem = 0x1308;
        ClosableTabControl tabs;

        public PaintOnDelete(ClosableTabControl tabs)
        {
            this.tabs = tabs;
            AssignHandle(tabs.Handle);
        }

        public Exception? Thrown { get; private set; }
        public bool Painted { get; private set; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == deleteItem)
            {
                try
                {
                    using var bitmap = new Bitmap(tabs.Width, tabs.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    tabs.PaintTabs(graphics);
                    Painted = true;
                }
                catch (Exception exception)
                {
                    Thrown = exception;
                }
            }

            base.WndProc(ref message);
        }

        public void Dispose() =>
            ReleaseHandle();
    }

    // A tab that isn't the one selected moves itself, and the selection stays where it was.
    [Test]
    public async Task MovingATabNotSelected()
    {
        using var tabs = Create(out _);
        tabs.SelectedIndex = 1;

        tabs.MoveTab(tabs.TabPages[0], 2);

        await Assert.That(Texts(tabs)).IsEquivalentTo(["Second", "Third", "First"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(tabs.SelectedTab!.Text).IsEqualTo("Second");
    }

    // A tab from another window goes in between the two it was dropped between, on the row, and
    // at the end beyond the last, or anywhere off the row.
    [Test]
    public async Task WhereADroppedTabGoes()
    {
        using var tabs = Create(out _);
        var first = tabs.GetTabRect(0);
        var last = tabs.GetTabRect(2);
        var middle = first.Top + first.Height / 2;
        Point Screen(int left, int top) =>
            tabs.PointToScreen(new(left, top));

        await Assert.That(tabs.DropIndex(Screen(first.Left + 2, middle))).IsEqualTo(0);
        await Assert.That(tabs.DropIndex(Screen(first.Right - 2, middle))).IsEqualTo(1);
        await Assert.That(tabs.DropIndex(Screen(last.Left + 2, middle))).IsEqualTo(2);
        await Assert.That(tabs.DropIndex(Screen(last.Right + 40, middle))).IsEqualTo(3);
        await Assert.That(tabs.DropIndex(Screen(first.Left + 2, 250))).IsEqualTo(3);
    }

    static List<string> Texts(TabControl tabs) =>
        tabs.TabPages.Cast<TabPage>().Select(_ => _.Text).ToList();

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

    // Closing one tab after another by clicking where the cross is, each tab coming under the
    // pointer as the one before it goes: Windows makes the second of two quick clicks a double
    // click, which closes its tab as a click does.
    [Test]
    public async Task ASecondQuickClickOnACrossClosesItsTabToo()
    {
        const int leftButtonDoubleClick = 0x0203;
        using var tabs = Create(out var closed);
        var tab = tabs.GetTabRect(1);

        Send(tabs, leftButtonDoubleClick, tab.Right - 12, tab.Top + tab.Height / 2);

        await Assert.That(closed).IsEquivalentTo(["Second"]);
        await Assert.That(tabs.SelectedTab!.Text).IsEqualTo("First");
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
