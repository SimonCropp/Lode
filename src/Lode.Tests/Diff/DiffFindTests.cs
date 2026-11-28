/// <summary>
/// The find bar over a diff: what is typed marked wherever it is in the rows shown, the match
/// gone to scrolled into view, down and sideways, F3 and Shift+F3, Enter and Shift+Enter going
/// from one to the next, round from the last to the first, and Escape closing the bar.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class DiffFindTests
{
    // Typed, every match in the rows shown is marked, and the first gone to; matching case, only
    // those that do. Closed, nothing is.
    [Test]
    public async Task TypingMarksEveryMatch()
    {
        using var stub = DiffSample.Stub();
        using var form = await Shown(DiffSample.File);
        var view = form.View;
        var canvas = view.Canvas;

        view.OpenFind();
        view.FindBar.Box.Text = "Document";

        // Each "document" of the lines both hunks show, a line deleted and those added among them.
        var expected = canvas.Rows
            .Select(_ => _.After ?? _.Before)
            .Sum(_ => _ == null ? 0 : Count(_.Content, "document"));
        await Assert.That(expected).IsGreaterThan(5);
        await Assert.That(canvas.Matches.Count).IsEqualTo(expected);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(0);
        await Assert.That(view.FindBar.CountText).IsEqualTo($"1 of {expected}");

        view.FindBar.MatchCase = true;
        await Assert.That(canvas.Matches.Count).IsEqualTo(0);
        await Assert.That(view.FindBar.CountText).IsEqualTo("No results");

        view.FindBar.Box.Text = "Written";
        await Assert.That(canvas.Matches.Count).IsEqualTo(DiffSample.Patch.Split("Written").Length - 1);

        view.CloseFind();
        await Assert.That(view.FindBar.Visible).IsFalse();
        await Assert.That(canvas.Matches).IsEmpty();
    }

    // F3 goes to each match in turn, from the first hunk into the second, and round from the last
    // to the first; Shift+F3 back, round the other way; Enter and Shift+Enter in the box too.
    [Test]
    public async Task F3AndShiftF3MoveBetweenMatchesAcrossHunks()
    {
        using var stub = DiffSample.Stub();
        using var form = await Shown(DiffSample.File);
        var view = form.View;
        var canvas = view.Canvas;
        view.OpenFind();
        view.FindBar.Box.Text = "previous";
        var hunkOf = canvas.Matches.Select(_ => canvas.Rows[_.Row].HunkIndex).ToList();
        await Assert.That(hunkOf).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(0);

        canvas.Focus();
        await Assert.That(Keystroke.Press(canvas, Keys.F3)).IsTrue();
        await Assert.That(canvas.CurrentMatch).IsEqualTo(1);
        var second = canvas.Matches[1];
        await Assert.That(canvas.TopRow).IsLessThanOrEqualTo(second.Row);
        await Assert.That(second.Row).IsLessThan(canvas.TopRow + 10);

        Keystroke.Press(canvas, Keys.F3);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(0);

        Keystroke.Press(canvas, Keys.Shift | Keys.F3);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(1);
        Keystroke.Press(canvas, Keys.Shift | Keys.F3);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(0);

        view.FindBar.FocusBox();
        Keystroke.Press(view.FindBar.Box, Keys.Enter);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(1);
        Keystroke.Press(view.FindBar.Box, Keys.Shift | Keys.Enter);
        await Assert.That(canvas.CurrentMatch).IsEqualTo(0);
    }

    // A match far along a long line, past where a line's whole is measured, is scrolled to
    // sideways, where it shows; the next, at the start of another line, back to the start.
    [Test]
    public async Task AMatchInALongLineScrollsSideways()
    {
        var long1 = string.Concat(Enumerable.Repeat("filler, ", 400)) + "needle();" + string.Concat(Enumerable.Repeat(" more", 100));
        var diff = (TextDiff) DiffParser.Parse($"--- a/long.js\n+++ b/long.js\n@@ -1,2 +1,2 @@\n-short();\n+{long1}\n needle();\n");
        using var stub = DiffSample.Stub(diff, [long1, "needle();"]);
        using var form = await Shown(new("long.js", null, GitChange.Modified));
        var view = form.View;
        var canvas = view.Canvas;
        await Assert.That(long1.Length).IsGreaterThan(3_000);

        view.OpenFind();
        view.FindBar.Box.Text = "needle";

        var match = canvas.Matches[canvas.CurrentMatch];
        await Assert.That(match.Start).IsEqualTo(long1.IndexOf("needle", StringComparison.Ordinal));
        await Assert.That(canvas.ScrolledAcross).IsGreaterThan(0);
        var start = canvas.PointOf(new(match.Row, match.Start), right: false).X;
        var end = canvas.PointOf(new(match.Row, match.End), right: false).X;
        await Assert.That(start).IsGreaterThan(0);
        await Assert.That(end).IsLessThan(canvas.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);

        canvas.FindNext(forward: true);
        await Assert.That(canvas.Matches[canvas.CurrentMatch].Start).IsEqualTo(0);
        await Assert.That(canvas.ScrolledAcross).IsEqualTo(0);
    }

    // Escape in the find bar closes it, the focus back on the lines, nothing marked.
    [Test]
    public async Task EscapeGivesTheFocusBackToTheDiff()
    {
        using var stub = DiffSample.Stub();
        using var form = await Shown(DiffSample.File);
        var view = form.View;
        var canvas = view.Canvas;
        canvas.Focus();
        await Assert.That(canvas.Focused).IsTrue();

        await Assert.That(Keystroke.Press(canvas, Keys.Control | Keys.F)).IsTrue();
        await Assert.That(view.FindBar.Visible).IsTrue();
        await Assert.That(view.FindBar.Box.Focused).IsTrue();
        view.FindBar.Box.Text = "document";
        await Assert.That(canvas.Matches).IsNotEmpty();

        await Assert.That(Keystroke.Press(view.FindBar.Box, Keys.Escape)).IsTrue();
        await Assert.That(view.FindBar.Visible).IsFalse();
        await Assert.That(canvas.Focused).IsTrue();
        await Assert.That(canvas.Matches).IsEmpty();

        // Opened again, what was typed is still there, selected, and found again.
        Keystroke.Press(canvas, Keys.Control | Keys.F);
        await Assert.That(view.FindBar.Box.SelectedText).IsEqualTo("document");
        await Assert.That(canvas.Matches).IsNotEmpty();
    }

    // Shown again, expanded or the other way, what is found is found in the rows as they are
    // now, the match gone to still the same line's; matches in the lines between hunks show once
    // they do.
    [Test]
    public async Task FoundAgainInTheRowsAsTheyAre()
    {
        using var stub = DiffSample.Stub();
        using var form = await Shown(DiffSample.File);
        var view = form.View;
        var canvas = view.Canvas;
        view.OpenFind();
        view.FindBar.Box.Text = "Step13(";
        await Assert.That(canvas.Matches).IsEmpty();

        canvas.ExpandWhole();
        await Assert.That(canvas.Matches.Count).IsEqualTo(1);
        await Assert.That(view.FindBar.CountText).IsEqualTo("1 found");

        view.FindBar.Box.Text = "written";
        canvas.FindNext(forward: true);
        var line = canvas.Rows[canvas.Matches[canvas.CurrentMatch].Row].After;
        await Assert.That(line).IsNotNull();
        view.SetSplit(true);
        var current = canvas.Matches[canvas.CurrentMatch];
        await Assert.That(canvas.Rows[current.Row].After).IsSameReferenceAs(line);
    }

    static int Count(string text, string find)
    {
        var count = 0;
        var at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(find, at + find.Length, StringComparison.OrdinalIgnoreCase);
        }

        return count;
    }

    static async Task<DiffForm> Shown(Change file)
    {
        var form = new DiffForm();
        form.Show();
        form.View.Show(new(DiffSample.Repo, file));
        await Pump.Until(() => form.View.Reading.IsCompleted);
        await form.View.Reading;
        return form;
    }

    // A window off screen holding the diff, which shows without taking the focus from whatever
    // has it, so the focus moves within it alone.
    sealed class DiffForm : Form
    {
        public DiffForm()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new(-20000, -20000);
            ClientSize = new(800, 400);
            View = new(new())
            {
                Dock = DockStyle.Fill
            };
            Controls.Add(View);
        }

        public DiffView View { get; }

        protected override bool ShowWithoutActivation => true;
    }
}

/// <summary>
/// A key pressed as the control with the focus gets it, through the keys of each control it is
/// in and then its window's, with Ctrl, Shift and Alt held as the key says: held for this thread
/// alone, as Windows keeps the keyboard's state for each, so nothing else running sees them.
/// </summary>
static class Keystroke
{
    const int keyDown = 0x0100;

    [DllImport("user32.dll")]
    static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    static extern bool SetKeyboardState(byte[] state);

    /// <summary>
    /// Whether anything did something with the key.
    /// </summary>
    public static bool Press(Control control, Keys keyData)
    {
        var before = new byte[256];
        GetKeyboardState(before);
        var held = (byte[]) before.Clone();
        held[(int) Keys.ControlKey] = Down((keyData & Keys.Control) != 0);
        held[(int) Keys.ShiftKey] = Down((keyData & Keys.Shift) != 0);
        held[(int) Keys.Menu] = Down((keyData & Keys.Alt) != 0);
        SetKeyboardState(held);
        try
        {
            var message = Message.Create(control.Handle, keyDown, (IntPtr) (keyData & Keys.KeyCode), IntPtr.Zero);
            return control.PreProcessMessage(ref message);
        }
        finally
        {
            SetKeyboardState(before);
        }
    }

    static byte Down(bool down) =>
        down ? (byte) 0x80 : (byte) 0;
}
