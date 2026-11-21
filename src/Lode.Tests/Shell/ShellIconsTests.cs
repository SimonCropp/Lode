/// <summary>
/// The overlays drawn over icons, as Explorer draws them.
/// </summary>
// The shell hands out its image list only on a thread that has COM, and ShellIcons keeps what
// it was given the first time. It hands out none while another thread is setting up its icon
// overlays, as asking for the shortcut's does, so these take turns with the tests that show
// icons.
[STAThreadExecutor]
[NotInParallel("windows")]
public class ShellIconsTests
{
    // A program's or shortcut's own icon is read off the thread that asks: until it is known, the
    // row has the icon every shortcut has, and is told once it is, when asking again gives it.
    [Test]
    public async Task AFilesOwnIconIsReadOffTheThreadThatAsks()
    {
        using var fixture = Fixture.Create();
        var shortcut = FileOperations.CreateShortcut(fixture.At("Docs"));
        var modified = File.GetLastWriteTime(shortcut);
        var told = 0;

        var first = ShellIcons.GetIcon(shortcut, false, modified, () => told++);
        await Pump.Until(() => told > 0);
        var known = ShellIcons.GetIcon(shortcut, false, modified, () => told++);
        await Pump.For(TimeSpan.FromMilliseconds(100));

        await Assert.That(first).IsNotEqualTo(ShellIcons.GetActualIcon(shortcut));
        await Assert.That(known).IsEqualTo(ShellIcons.GetActualIcon(shortcut));
        await Assert.That(told).IsEqualTo(1);
    }

    // IDO_SHGIOI_LINK: the shell's shortcut arrow.
    const int linkOverlayId = 0x0FFFFFFE;

    // Shortcuts are told by their type, as the shell tells them, whatever case their extension
    // is in. Nothing else gets the arrow, nor does a folder, whatever it is called.
    [Test]
    public Task OverlayByType()
    {
        (string Name, bool IsDirectory)[] items =
        [
            ("DbToMermaid.slnx.lnk", false),
            ("UPPER.LNK", false),
            ("Site.url", false),
            ("Program.pif", false),
            ("Tool.appref-ms", false),
            ("notes.txt", false),
            ("setup.exe", false),
            ("notes.aleph", false),
            ("Documents.library-ms", false),
            ("README", false),
            ("Folder.lnk", true)
        ];
        return Verify(items.ToDictionary(
            _ => $"{_.Name} (directory: {_.IsDirectory})",
            _ => Describe(ShellIcons.GetOverlay($@"C:\{_.Name}", _.IsDirectory))));
    }

    static string Describe(int overlay)
    {
        if (overlay == 0)
        {
            return "none";
        }

        if (overlay == ShellIcons.ShortcutOverlay)
        {
            return "shortcut arrow";
        }

        return $"overlay {overlay}";
    }

    // Lode doesn't ask the shell, which would load every overlay handler installed into it.
    [Test]
    public async Task ShortcutOverlayIsTheShellsArrow()
    {
        // ShellIcons has its image list before the overlays are set up.
        _ = ShellIcons.SmallImageList;

        await Assert.That(SHGetIconOverlayIndex(IntPtr.Zero, linkOverlayId)).IsEqualTo(ShellIcons.ShortcutOverlay);
    }

    // The arrow goes over the icon's bottom left corner, leaving the rest of it as it was.
    [Test]
    public async Task TheArrowIsDrawnInTheBottomLeftCorner()
    {
        var icon = ShellIcons.GetIcon("notes.txt", false);
        var size = ShellIcons.SmallIconSize;
        using var plain = ShellIcons.Render(icon, size, Point.Empty);
        using var shortcut = ShellIcons.Render(icon, size, Point.Empty, ShellIcons.ShortcutOverlay);

        var changed = new List<Point>();
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                if (plain.GetPixel(x, y) != shortcut.GetPixel(x, y))
                {
                    changed.Add(new(x, y));
                }
            }
        }

        await Assert.That(changed).IsNotEmpty();
        await Assert.That(changed.Max(_ => _.X)).IsLessThan(size.Width * 2 / 3);
        await Assert.That(changed.Min(_ => _.Y)).IsGreaterThanOrEqualTo(size.Height / 3);
    }

    // Pixel for pixel, the icon the shell makes for a shortcut, its overlays added, which is
    // what Explorer shows.
    [Test]
    public async Task AShortcutLooksAsTheShellDrawsIt()
    {
        using var fixture = Fixture.Create();
        var shortcut = fixture.At("notes.lnk");
        ShellLink.Save(shortcut, fixture.At("notes.aleph"));
        var size = ShellIcons.SmallIconSize;

        using var lode = ShellIcons.Render(
            ShellIcons.GetIcon(shortcut, false),
            size,
            Point.Empty,
            ShellIcons.GetOverlay(shortcut, false));
        using var shell = ShellsIcon(shortcut, size);

        await Assert.That(Differences(lode, shell)).IsEqualTo(0);
    }

    // Drawn onto a canvas as ShellIcons.Render draws, so only what the icons hold can differ.
    static Bitmap ShellsIcon(string path, Size size)
    {
        const uint icon = 0x100;
        const uint smallIcon = 0x1;
        const uint addOverlays = 0x20;
        var info = new NativeMethods.ShellFileInfo();
        NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.ShellFileInfo>(), icon | smallIcon | addOverlays);
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var source = Icon.FromHandle(info.Icon);
            using var image = source.ToBitmap();
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImage(image, new Rectangle(Point.Empty, image.Size));
        }
        finally
        {
            NativeMethods.DestroyIcon(info.Icon);
        }

        return bitmap;
    }

    static int Differences(Bitmap first, Bitmap second)
    {
        var count = 0;
        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                if (first.GetPixel(x, y) != second.GetPixel(x, y))
                {
                    count++;
                }
            }
        }

        return count;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetIconOverlayIndex(IntPtr iconPath, int iconIndex);
}
