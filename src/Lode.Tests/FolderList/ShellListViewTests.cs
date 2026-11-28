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

    // Past as many as they hold, the places of the icons used least lately are given to new
    // ones: an icon asked for all along keeps its place, and each place shows the icon now in it.
    [Test]
    public async Task TheIconsHeldStayUnderTheirCap()
    {
        using var icons = new ShellListView.PaddedIcons(8, Solid);
        var set = icons.For(96);
        var kept = icons.Index(1000, 0);
        set.Show(kept);
        for (var icon = 0; icon < 100; icon++)
        {
            set.Show(icons.Index(icon, 0));
            await Assert.That(icons.Index(1000, 0)).IsEqualTo(kept);
            await Assert.That(icons.Count).IsLessThanOrEqualTo(8);
        }

        await Assert.That(ImageCount(set.Handle)).IsLessThanOrEqualTo(8);
        for (var icon = 96; icon < 100; icon++)
        {
            var index = icons.Index(icon, 0);
            set.Show(index);
            await Assert.That(ColorAt(set, index)).IsEqualTo(ColorOf(icon, 0).ToArgb());
        }

        await Assert.That(ColorAt(set, kept)).IsEqualTo(ColorOf(1000, 0).ToArgb());
        await Assert.That(icons.Count).IsLessThanOrEqualTo(8);
    }

    // On a monitor at another scale, a list's icons are drawn for it as the list draws its rows:
    // those it shows, not every icon there is, which on the first move would all be drawn at
    // once. The rows keep their indexes.
    [Test]
    public async Task AScaleChangeDrawsOnlyTheIconsAskedFor()
    {
        var drawn = 0;
        using var icons = new ShellListView.PaddedIcons(1000, (icon, canvas, offset, overlay, size) =>
        {
            drawn++;
            return Solid(icon, canvas, offset, overlay, size);
        });
        using var form = Host();
        using var list = new ShellListView
        {
            VirtualMode = true,
            View = View.Details,
            Dock = DockStyle.Fill,
            Icons = icons
        };
        list.Columns.Add("Name", 200);
        var items = Enumerable.Range(0, 500)
            .Select(_ => new ListViewItem($"Item {_}", icons.Index(_, 0)))
            .ToArray();
        list.RetrieveVirtualItem += (_, args) => args.Item = items[args.ItemIndex];
        list.VirtualListSize = items.Length;
        form.Controls.Add(list);
        form.Show();
        using var bitmap = new Bitmap(list.Width, list.Height);

        list.DrawToBitmap(bitmap, new(0, 0, list.Width, list.Height));
        var shown = drawn;
        list.UseImagesFor(list.DeviceDpi * 2);
        var onMoving = drawn - shown;
        list.DrawToBitmap(bitmap, new(0, 0, list.Width, list.Height));
        var atTwice = icons.For(list.DeviceDpi * 2).Drawn;

        await Assert.That(shown).IsGreaterThan(0);
        await Assert.That(shown).IsLessThan(50);
        await Assert.That(onMoving).IsEqualTo(0);
        await Assert.That(atTwice).IsGreaterThan(0);
        await Assert.That(atTwice).IsLessThanOrEqualTo(shown);
        await Assert.That(drawn).IsEqualTo(shown + atTwice);
        await Assert.That(icons.For(list.DeviceDpi).Drawn).IsEqualTo(shown);
    }

    // An icon's own colour, filling its whole image, so what a place shows says which icon is
    // in it.
    internal static Color ColorOf(int icon, int overlay) =>
        Color.FromArgb(255, 30 + icon * 37 % 200, 30 + overlay * 61 % 200, 30 + icon / 200 % 200);

    internal static Bitmap Solid(int icon, Size canvas, Point offset, int overlay, int iconSize)
    {
        var bitmap = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(ColorOf(icon, overlay));
        return bitmap;
    }

    // The colour in the middle of the image at a place, as the set draws it.
    static int ColorAt(ShellListView.PaddedIcons.Set set, int index)
    {
        ImageList_GetIconSize(set.Handle, out var width, out var height);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var context = graphics.GetHdc();
            ImageList_Draw(set.Handle, index, context, 0, 0, 0);
            graphics.ReleaseHdc(context);
        }

        return bitmap.GetPixel(width / 2, height / 2).ToArgb();
    }

    static Form Host() =>
        new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(400, 300)
        };

    // Showing a window normally takes the focus from whatever the developer is doing.
    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
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

    [DllImport("comctl32.dll")]
    static extern bool ImageList_GetIconSize(IntPtr imageList, out int width, out int height);

    [DllImport("comctl32.dll")]
    static extern bool ImageList_Draw(IntPtr imageList, int index, IntPtr context, int x, int y, int style);
}
