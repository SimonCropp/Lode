[WindowThread]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ShellListViewTests
{
    // Every list shows the one set of padded icons. A list that closes, as a tab does, must
    // leave it for the rest.
    [Test]
    public async Task ClosingAListLeavesTheOthersTheirIcons()
    {
        using var kept = new ShellListView();
        _ = kept.Handle;
        var size = ShellListView.PaddedIconSize(kept.DeviceDpi);

        var closed = new ShellListView();
        _ = closed.Handle;
        closed.Dispose();

        await Assert.That(size.Height).IsGreaterThan(0);
        await Assert.That(ShellListView.PaddedIconSize(kept.DeviceDpi)).IsEqualTo(size);
    }

    // Rows on a monitor at another scale have images, and so a height, for that scale, rather
    // than for the monitor the first list was on. The same icon is at the same index at each,
    // so the rows keep theirs.
    [Test]
    public async Task RowsFollowTheMonitorsScale()
    {
        using var list = new ShellListView();
        _ = list.Handle;
        var dpi = list.DeviceDpi;
        var icon = ShellListView.PaddedIcon(ShellIcons.FolderIcon);
        var first = list.RowImages;
        var size = ShellListView.PaddedIconSize(dpi);

        list.UseImagesFor(dpi * 2);
        var doubled = ShellListView.PaddedIconSize(dpi * 2);

        await Assert.That(list.RowImages).IsNotEqualTo(first);
        await Assert.That(doubled.Height).IsEqualTo(size.Height * 2);
        await Assert.That(ShellListView.PaddedIcon(ShellIcons.FolderIcon)).IsEqualTo(icon);
        await Assert.That(ImageCount(list.RowImages)).IsEqualTo(ImageCount(first));
    }

    // Printable Latin, measured a character at a time, is as wide as the list makes it whole,
    // letters a font kerns, such as AV and To, among them. Anything else is measured whole.
    [Test]
    public async Task MeasuringTextAgreesWithTheList()
    {
        using var list = new ShellListView();
        _ = list.Handle;
        var measure = new ShellListView.TextMeasure(list);
        string[] texts =
        [
            "",
            "AVATAR WAVE.txt",
            "Today's To-do (final) v2.docx",
            "LT.YT.F,P.\"A'A",
            "iiii WWWW 1111 mmmm",
            "Übersicht Straße café naïve ±½©.pdf",
            "Œuvre ŁÓDŹ žluťoučký kůň.png",
            "日本語のファイル.txt",
            "تقرير.docx",
            "👍🏽 thumbs.png",
            "áè"
        ];
        foreach (var text in texts)
        {
            await Assert.That(measure.Width(text)).IsEqualTo(list.TextWidth(text));
        }
    }

    [DllImport("comctl32.dll")]
    static extern int ImageList_GetImageCount(IntPtr imageList);

    static int ImageCount(IntPtr imageList) =>
        ImageList_GetImageCount(imageList);
}
