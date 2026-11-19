[STAThreadExecutor]
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

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    static extern int ImageList_GetImageCount(IntPtr imageList);

    static int ImageCount(IntPtr imageList) =>
        ImageList_GetImageCount(imageList);
}
