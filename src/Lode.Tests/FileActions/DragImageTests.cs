/// <summary>
/// The picture a drag carries, which the shell frames and moves with the pointer, as it does
/// Explorer's.
/// </summary>
[STAThreadExecutor]
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

#if DEBUG
    // Pixels depend on the machine's icons and fonts, so, as with the other captures, these only
    // run in Debug builds.

    // One item: its icon, as big as the picture.
    [Test]
    public Task One() =>
        Capture([(ShellIcons.GetTypeIcon("Docs", true), 0)], 1);

    // Several: the first three stacked, the first in front, and how many there are.
    [Test]
    public Task Several() =>
        Capture(
            [
                (ShellIcons.GetTypeIcon("Docs", true), 0),
                (ShellIcons.GetTypeIcon("notes.txt", false), 0),
                (ShellIcons.GetTypeIcon("setup.ini", false), 0)
            ],
            12);

    static async Task Capture(IReadOnlyList<(int Icon, int Overlay)> icons, int count)
    {
        using var picture = DragImage.Picture(icons, count, 96);
        var stream = new MemoryStream();
        picture.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        await Verify(stream, "png");
    }
#endif
}
