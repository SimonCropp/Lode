/// <summary>
/// The window's bars on a monitor at another scale than the one they were made at, as when the
/// window moves from a laptop at 150% to a monitor at 100%: their icons, drawn at a size, and
/// their spacing, set in pixels, are for that scale, as the text beside them is. Tests run
/// unaware of scale, so a bar is told the scale it is on, as the window moving would tell it,
/// then told by a message, as Windows tells it, that it has moved back to the scale it is at.
/// </summary>
[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class MonitorScaleTests
{
    // WM_DPICHANGED_AFTERPARENT, which Windows sends each control in a window that has moved to
    // a monitor at another scale.
    const int dpiChangedAfterParent = 0x02E3;

    // Its buttons' icons and spacing, See more's icon, and the bar's own spacing, twice the size
    // at twice the scale.
    [Test]
    public async Task ActionsBarFollowsTheMonitorsScale()
    {
        using var window = new Form();
        var bar = new ActionsBar(window, new(), () => null, _ => { }, _ => { });
        window.Controls.Add(bar.Strip);
        _ = bar.Strip.Handle;
        var dpi = bar.Strip.DeviceDpi;
        var before = Sizes(bar);

        bar.ScaleTo(dpi * 2);
        var doubled = Sizes(bar);
        NativeMethods.SendMessage(bar.Strip.Handle, dpiChangedAfterParent, IntPtr.Zero, IntPtr.Zero);

        await Assert.That(doubled.Icon).IsEqualTo(before.Icon * 2);
        await Assert.That(doubled.Button).IsEqualTo(Doubled(before.Button));
        await Assert.That(doubled.SeeMore).IsEqualTo(before.SeeMore * 2);
        await Assert.That(doubled.Strip).IsEqualTo(Doubled(before.Strip));
        await Assert.That(Sizes(bar)).IsEqualTo(before);
    }

    record ActionsBarSizes(int Icon, Padding Button, int SeeMore, Padding Strip);

    static ActionsBarSizes Sizes(ActionsBar bar)
    {
        var button = bar.Strip.Items.OfType<ToolStripButton>().First();
        var seeMore = bar.Strip.Items["seeMore"]!;
        return new(button.Image!.Width, button.Padding, seeMore.Image!.Width, bar.Strip.Padding);
    }

    // The branch's and the tag's icons, and the bar's spacing, twice the size at twice the scale.
    [Test]
    public async Task StatusBarFollowsTheMonitorsScale()
    {
        using var window = new Form();
        using var bar = new StatusBar(window, new(), Updates.Instance, () => null, _ => { }, _ => { }, _ => { });
        window.Controls.Add(bar.Strip);
        _ = bar.Strip.Handle;
        var dpi = bar.Strip.DeviceDpi;
        var before = Sizes(bar);

        bar.ScaleTo(dpi * 2);
        var doubled = Sizes(bar);
        NativeMethods.SendMessage(bar.Strip.Handle, dpiChangedAfterParent, IntPtr.Zero, IntPtr.Zero);

        await Assert.That(doubled.Branch).IsEqualTo(before.Branch * 2);
        await Assert.That(doubled.Tag).IsEqualTo(before.Tag * 2);
        await Assert.That(doubled.Strip).IsEqualTo(Doubled(before.Strip));
        await Assert.That(Sizes(bar)).IsEqualTo(before);
    }

    record StatusBarSizes(Size Branch, Size Tag, Padding Strip);

    static StatusBarSizes Sizes(StatusBar bar) =>
        new(bar.Strip.Items["git"]!.Image!.Size, bar.Strip.Items["gitTag"]!.Image!.Size, bar.Strip.Padding);

    static Padding Doubled(Padding padding) =>
        new(padding.Left * 2, padding.Top * 2, padding.Right * 2, padding.Bottom * 2);

    // The address bar's chevrons are sized from its font, which the window scales for the
    // monitor it is on, as it does every font in it, rather than kept at the size they have at
    // the scale Windows started at. At the window's own font, the size they always were.
    [Test]
    public async Task AddressBarChevronsFollowTheFont()
    {
        using var window = new Form();
        var breadcrumb = new Breadcrumb(new());
        window.Controls.Add(breadcrumb);
        var size = breadcrumb.ChevronFont.SizeInPoints;

        window.Font = new(window.Font.FontFamily, window.Font.SizeInPoints * 2);

        await Assert.That(size).IsEqualTo(7f);
        await Assert.That(breadcrumb.ChevronFont.SizeInPoints).IsEqualTo(size * 2);
    }
}
