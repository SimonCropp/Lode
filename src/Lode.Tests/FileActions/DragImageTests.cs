/// <summary>
/// The picture a drag carries, which the shell frames and moves with the pointer, as it does
/// Explorer's.
/// </summary>
[WindowThread]
// The shell has one drag helper for the process, which keeps the picture it was last given:
// two given at once, one drag can get the other's. So these take turns with the window tests,
// whose drags have pictures too.
[NotInParallel("windows")]
public class DragImageTests
{
    // The size the shell's own pictures are, at the display's scale.
    [Test]
    [Arguments(96, 96)]
    [Arguments(144, 144)]
    public async Task ADragCarriesItsPicture(int dpi, int side)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\Docs" });
        DragImage.Set(data, [(ShellIcons.GetTypeIcon("Docs", true), 0)], 1, dpi);

        await Assert.That(DragImage.SizeOf(data)).IsEqualTo(new Size(side, side));
    }

    // The shell's icons are in the picture, the one item's across it, and of several, the first
    // where only it is, at the left, and the third where only it is, at the right: what the
    // icons look like is the machine's, so the captures below draw icons of their own.
    [Test]
    public async Task TheShellsIconsAreDrawn()
    {
        var folder = ShellIcons.GetTypeIcon("Docs", true);
        using (var one = DragImage.Picture([(folder, 0)], 1, 96))
        {
            await Assert.That(AnyDrawn(one, new(0, 0, 96, 96))).IsTrue();
        }

        // At 96 dots per inch each is 72 square, 12 further up and right than the one before.
        using var several = DragImage.Picture([(folder, 0), (folder, 0), (folder, 0)], 3, 96);
        await Assert.That(AnyDrawn(several, new(0, 24, 12, 72))).IsTrue();
        await Assert.That(AnyDrawn(several, new(84, 0, 12, 72))).IsTrue();
    }

    static bool AnyDrawn(Bitmap picture, Rectangle area)
    {
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                if (picture.GetPixel(x, y).A > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

#if DEBUG
    // Pixels depend on the machine's fonts, so, as with the other captures, these only run in
    // Debug builds.

    // One item: its icon, as big as the picture.
    [Test]
    public Task One() =>
        Capture(1, 1);

    // Several: the first three stacked, the first in front, and how many there are.
    [Test]
    public Task Several() =>
        Capture(3, 12);

    static async Task Capture(int icons, int count)
    {
        using var picture = DragImage.Picture(icons, count, 96, Icon);
        var stream = new MemoryStream();
        picture.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        await Verify(stream, "png");
    }

    // An icon as big as asked, its own colour for each, with an edge and a mark in its top left
    // corner, so which is in front, and how each is placed and scaled, shows.
    static Bitmap Icon(int index, int size)
    {
        Color[] fills = [Color.SteelBlue, Color.SeaGreen, Color.Goldenrod];
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var inset = size / 8;
        var body = new Rectangle(inset, inset, size - inset * 2, size - inset * 2);
        using (var fill = new SolidBrush(fills[index % fills.Length]))
        {
            graphics.FillRectangle(fill, body);
        }

        using (var edge = new Pen(Color.Black, Math.Max(1, size / 32)))
        {
            graphics.DrawRectangle(edge, body);
        }

        graphics.FillRectangle(Brushes.White, body.Left + inset / 2, body.Top + inset / 2, inset, inset);
        return bitmap;
    }
#endif
}
