[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ActionsStripTests
{
    const int rightButtonDown = 0x0204;
    const int rightButtonUp = 0x0205;

    // A greyed out button has no mouse events, so its right-click is still the strip's to say,
    // at the point it was right-clicked, over that button. Nor does it click the button.
    [Test]
    public async Task RightClickOverAGreyedOutButton()
    {
        using var form = new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(400, 100)
        };
        var strip = new ActionsStrip();
        var enabled = new ToolStripButton("Copy");
        var greyed = new ToolStripButton("Rename")
        {
            Enabled = false
        };
        strip.Items.AddRange(enabled, greyed);
        form.Controls.Add(strip);
        form.Show();
        var clicked = new List<string>();
        enabled.Click += (_, _) => clicked.Add("Copy");
        var requested = new List<string>();
        strip.MenuRequested += screen => requested.Add(strip.GetItemAt(strip.PointToClient(screen))?.Text ?? "nothing");

        RightClick(strip, greyed);
        RightClick(strip, enabled);

        await Assert.That(requested).IsEquivalentTo(["Rename", "Copy"], CollectionOrdering.Matching);
        await Assert.That(clicked).IsEmpty();
    }

    // So too in a parent's drop-down, open, where a right-click on an action greyed out is the
    // list's to say, and a right-click on one that isn't doesn't run it.
    [Test]
    public async Task RightClickInADropDown()
    {
        using var form = new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(400, 100)
        };
        var strip = new ActionsStrip();
        using var list = new ActionsDropDown();
        var git = new ToolStripDropDownButton("Git")
        {
            DropDown = list
        };
        var pull = new ToolStripMenuItem("Pull");
        var push = new ToolStripMenuItem("Push")
        {
            Enabled = false
        };
        git.DropDownItems.AddRange(pull, push);
        strip.Items.Add(git);
        form.Controls.Add(strip);
        form.Show();
        var clicked = new List<string>();
        pull.Click += (_, _) => clicked.Add("Pull");
        var requested = new List<string>();
        list.MenuRequested += screen => requested.Add(list.GetItemAt(list.PointToClient(screen))?.Text ?? "nothing");
        git.ShowDropDown();

        RightClick(list, push);
        RightClick(list, pull);
        git.HideDropDown();

        await Assert.That(requested).IsEquivalentTo(["Push", "Pull"], CollectionOrdering.Matching);
        await Assert.That(clicked).IsEmpty();
    }

    static void RightClick(ToolStrip strip, ToolStripItem item)
    {
        var point = new Point(item.Bounds.Left + 5, item.Bounds.Top + 5);
        var packed = (point.Y << 16) | (point.X & 0xFFFF);
        NativeMethods.SendMessage(strip.Handle, rightButtonDown, 2, packed);
        NativeMethods.SendMessage(strip.Handle, rightButtonUp, 0, packed);
    }

    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
