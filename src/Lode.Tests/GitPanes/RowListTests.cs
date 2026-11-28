/// <summary>
/// The lists of the Changes and History panes: one column across, fitted to the room the rows
/// leave it.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class RowListTests
{
    // However many rows come or go, and however the list narrows, the column fits beside the
    // scrollbar the rows bring or take away, with none along the bottom: the list brought one
    // whenever the column was wider than the room for a moment, more rows than fit coming or the
    // list narrowing, and kept it once the column fitted.
    [Test]
    public async Task NoScrollbarAlongBottom()
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            FormBorderStyle = FormBorderStyle.None,
            ClientSize = new(726, 300)
        };
        var list = new RowList(57);
        form.Controls.Add(list);
        form.Show();
        Application.DoEvents();

        form.ClientSize = new(474, 300);
        Application.DoEvents();
        await Assert.That(Scrollbars(list)).IsEqualTo("none");

        foreach (var rows in (int[]) [3, 200, 0, 200, 3, 5, 6, 5, 0])
        {
            list.SetRows(rows);
            Application.DoEvents();
            await Assert.That(Scrollbars(list)).IsEqualTo(rows * 57 > 300 ? "side" : "none");
            await Assert.That(list.Columns[0].Width).IsEqualTo(list.ClientSize.Width);
        }

        list.SetRows(200);
        form.ClientSize = new(380, 300);
        Application.DoEvents();
        await Assert.That(Scrollbars(list)).IsEqualTo("side");
        await Assert.That(list.Columns[0].Width).IsEqualTo(list.ClientSize.Width);
    }

    // Given rows while hidden, as the stashes under Changes' files are, then shown, the list is
    // sized as its window is made, before that window is told how many rows there are: it fits
    // its column to the row height asked for then, rather than asking for the first row's bounds,
    // which the list hasn't got, and which threw from the size changing, failing the test as
    // WindowThread keeps what a window's handler throws. Once it has rows, the height is theirs.
    [Test]
    public async Task SizedAsItsWindowIsMadeWithRowsAlready()
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            FormBorderStyle = FormBorderStyle.None,
            ClientSize = new(400, 300)
        };
        var list = new RowList(29)
        {
            Dock = DockStyle.None,
            Size = new(200, 100),
            Visible = false
        };
        list.SetRows(3);
        form.Controls.Add(list);
        form.Show();
        Application.DoEvents();

        list.Visible = true;
        Application.DoEvents();

        await Assert.That(list.IsHandleCreated).IsTrue();
        await Assert.That(list.RowHeight()).IsEqualTo(list.GetItemRect(0).Height);
        await Assert.That(list.Columns[0].Width).IsEqualTo(list.ClientSize.Width);
    }

    // The scrollbars taking room from the rows: along the side, the bottom, both or neither.
    internal static string Scrollbars(RowList list)
    {
        var side = list.ClientSize.Width < list.Width;
        var bottom = list.ClientSize.Height < list.Height;
        if (side &&
            bottom)
        {
            return "side and bottom";
        }

        if (bottom)
        {
            return "bottom";
        }

        if (side)
        {
            return "side";
        }

        return "none";
    }
}
