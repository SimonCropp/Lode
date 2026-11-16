[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class PaneSplitContainerTests
{
    const int leftButtonDown = 0x0201;
    const int leftButtonUp = 0x0202;
    const int mouseMove = 0x0200;
    const int distance = 200;

    // As in File Explorer, a click on the splitter that does not drag it leaves it alone, and
    // the focus where it was. The split's own active control, not the form's: the form's is the
    // outermost container holding the focus, the split either way. A SplitContainer takes it
    // from the box when its splitter is pressed.
    [Test]
    public async Task ClickingTheSplitterLeavesTheFocusInThePane()
    {
        using var form = Create(out var split, out var box);
        var grip = Grip(split);

        Send(split, leftButtonDown, grip);
        Send(split, leftButtonUp, grip);

        await Assert.That(split.ActiveControl).IsSameReferenceAs(box);
        await Assert.That(split.SplitterDistance).IsEqualTo(distance);
    }

    // The panes follow the mouse as it moves, rather than when it lets go.
    [Test]
    public async Task DraggingTheSplitterResizesThePanesAsItGoes()
    {
        using var form = Create(out var split, out var box);
        var grip = Grip(split);
        var moved = grip with { X = grip.X + 60 };

        Send(split, leftButtonDown, grip);
        Send(split, mouseMove, moved);
        var whileDragging = split.SplitterDistance;
        Send(split, leftButtonUp, moved);

        await Assert.That(whileDragging).IsEqualTo(distance + 60);
        await Assert.That(split.SplitterDistance).IsEqualTo(distance + 60);
        await Assert.That(split.ActiveControl).IsSameReferenceAs(box);
    }

    // Each move paints the panes before the next is taken, not once the mouse stops, so what a
    // pane grows into is drawn straight away rather than left showing what was there before,
    // which for the folder tree, painted through a buffer, was its scrollbar at every width it
    // passed through.
    [Test]
    public async Task DraggingTheSplitterPaintsThePanesAsItGoes()
    {
        using var form = Create(out var split, out _);
        var pane = new PaintedWidth
        {
            Dock = DockStyle.Fill
        };
        split.Panel1.Controls.Add(pane);
        // Windows paints nothing of a window wholly off the screen, as the form is, unless it
        // is layered, which any opacity short of full makes it.
        form.Opacity = 0.99;
        Application.DoEvents();
        var grip = Grip(split);
        var moved = grip with { X = grip.X + 60 };

        Send(split, leftButtonDown, grip);
        Send(split, mouseMove, moved);
        var painted = pane.Painted;
        Send(split, leftButtonUp, moved);

        await Assert.That(painted).IsEqualTo(distance + 60);
    }

    // Past where a pane stops shrinking, the splitter stays at the limit.
    [Test]
    public async Task DraggingPastThePaneMinimumStopsAtIt()
    {
        using var form = Create(out var split, out _);
        var grip = Grip(split);
        var past = grip with { X = -500 };

        Send(split, leftButtonDown, grip);
        Send(split, mouseMove, past);
        Send(split, leftButtonUp, past);

        await Assert.That(split.SplitterDistance).IsEqualTo(split.Panel1MinSize);
    }

    // After a drag, moving the mouse moves nothing.
    [Test]
    public async Task MovingAfterLettingGoLeavesTheSplitter()
    {
        using var form = Create(out var split, out _);
        var grip = Grip(split);

        Send(split, leftButtonDown, grip);
        Send(split, leftButtonUp, grip);
        Send(split, mouseMove, grip with { X = grip.X + 60 });

        await Assert.That(split.SplitterDistance).IsEqualTo(distance);
    }

    // Explorer's resize arrows over the splitter, not SplitContainer's split cursor, which has a
    // bar between them, and nothing of its own over the panes.
    [Test]
    public async Task TheSplitterShowsExplorersResizeCursor()
    {
        using var form = Create(out var split, out _);

        await Assert.That(split.CursorAt(Grip(split))).IsSameReferenceAs(Cursors.SizeWE);
        await Assert.That(split.CursorAt(new(10, 10))).IsNull();
    }

    static Form Create(out PaneSplitContainer split, out TextBox box)
    {
        var form = new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(800, 400)
        };
        split = new()
        {
            Dock = DockStyle.Fill
        };
        box = new();
        split.Panel2.Controls.Add(box);
        form.Controls.Add(split);
        form.Show();
        split.SplitterDistance = distance;
        form.ActiveControl = box;
        return form;
    }

    // The middle of the splitter.
    static Point Grip(SplitContainer split)
    {
        var splitter = split.SplitterRectangle;
        return new(splitter.Left + splitter.Width / 2, splitter.Top + splitter.Height / 2);
    }

    static void Send(Control control, int message, Point point) =>
        NativeMethods.SendMessage(control.Handle, message, 1, (point.Y << 16) | (point.X & 0xFFFF));

    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    // How wide it was when last painted.
    sealed class PaintedWidth : Control
    {
        public int Painted { get; private set; }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            Painted = Width;
            base.OnPaint(eventArgs);
        }
    }
}
