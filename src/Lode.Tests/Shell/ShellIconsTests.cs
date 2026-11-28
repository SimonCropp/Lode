/// <summary>
/// The overlays drawn over icons, as Explorer draws them.
/// </summary>
// The shell hands out its image list only on a thread that has COM, and ShellIcons keeps what
// it was given the first time. It hands out none while another thread is setting up its icon
// overlays, as asking for the shortcut's does, so these take turns with the tests that show
// icons.
[WindowThread]
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
            _ => Describe(ShellIcons.GetOverlay($@"C:\{_.Name}", _.IsDirectory))))
            .Snapshot(
                """
                {
                  DbToMermaid.slnx.lnk (directory: False): shortcut arrow,
                  Documents.library-ms (directory: False): none,
                  Folder.lnk (directory: True): none,
                  notes.aleph (directory: False): none,
                  notes.txt (directory: False): none,
                  Program.pif (directory: False): shortcut arrow,
                  README (directory: False): none,
                  setup.exe (directory: False): none,
                  Site.url (directory: False): shortcut arrow,
                  Tool.appref-ms (directory: False): shortcut arrow,
                  UPPER.LNK (directory: False): shortcut arrow
                }
                """);
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

    // A type nothing is registered for is named after its extension, in lower case rather than
    // the capitals Windows gives it. One that is registered, or a file with no extension, is as
    // Windows has it.
    [Test]
    public async Task AnUnregisteredTypeKeepsItsExtensionInLowerCase()
    {
        await Assert.That(ShellIcons.Unregistered("ALEPH File", ".aleph")).IsEqualTo("aleph File");
        await Assert.That(ShellIcons.Unregistered("BETA File", ".Beta")).IsEqualTo("beta File");
        await Assert.That(ShellIcons.Unregistered("Text Document", ".txt")).IsEqualTo("Text Document");
        await Assert.That(ShellIcons.Unregistered("TXT File", ".csv")).IsEqualTo("TXT File");
        await Assert.That(ShellIcons.Unregistered("File", "")).IsEqualTo("File");
    }

    [Test]
    public async Task TheTypeColumnSaysAnUnregisteredTypeInLowerCase()
    {
        var typeName = ShellIcons.GetTypeName(@"C:\nowhere\notes.lodeunregistered", false);

        await Assert.That(typeName).IsEqualTo("lodeunregistered File");
    }

    // A console has its own icon, read from the file, as a program has: its type registers the
    // file itself as its icon, with a handler to read it, so Computer Management and Services
    // each show theirs, rather than the one every console would have from its type alone.
    [Test]
    public async Task AConsoleHasItsOwnIcon()
    {
        var console = Path.Combine(Environment.SystemDirectory, "compmgmt.msc");

        var icon = ShellIcons.GetIcon(console, false);

        await Assert.That(icon).IsEqualTo(ShellIcons.GetActualIcon(console));
        await Assert.That(icon).IsNotEqualTo(ShellIcons.GetTypeIcon(console, false));
    }

    // Whether each file of a type has its own icon is read from how the type is registered: an
    // icon of "%1", the file itself, or an icon handler to give it, on what the extension is
    // registered as, on the extension itself, on what the user chose to open it with, or on what
    // Windows registers for the extension whatever opens it. A type whose icon is in a library
    // has that one for every file, as does one registered as nothing.
    [Test]
    public Task WhetherEachFileOfATypeHasItsOwnIconIsReadFromTheRegistry()
    {
        using var scratch = new RegistryScratch();
        scratch.Set(".lodeitself", null, "Lode.Itself");
        scratch.Set(@"Lode.Itself\DefaultIcon", null, "%1");
        scratch.Set(".lodequoted", null, "Lode.Quoted");
        scratch.Set(@"Lode.Quoted\DefaultIcon", null, "\"%1\"");
        scratch.Set(".lodehandled", null, "Lode.Handled");
        scratch.Set(@"Lode.Handled\shellex\IconHandler", null, "{7A80E4A8-8005-11D2-BCF8-00C04F72C717}");
        scratch.Set(".lodelibrary", null, "Lode.Library");
        scratch.Set(@"Lode.Library\DefaultIcon", null, @"C:\Windows\System32\imageres.dll,-67");
        scratch.Set(@".lodeextension\DefaultIcon", null, "%1");
        scratch.Set(".lodesystem", null, "Lode.Library");
        scratch.Set(@"SystemFileAssociations\.lodesystem\shellex\IconHandler", null, "{7A80E4A8-8005-11D2-BCF8-00C04F72C717}");
        scratch.Set(".lodechosen", null, "Lode.Library");
        scratch.Set(".lodechosenlatest", null, "Lode.Library");
        var choicesPath = $@"{scratch.Root}\FileExts";
        using (var choice = Registry.CurrentUser.CreateSubKey($@"{choicesPath}\.lodechosen\UserChoice"))
        {
            choice.SetValue("ProgId", "Lode.Itself");
        }

        using (var choice = Registry.CurrentUser.CreateSubKey($@"{choicesPath}\.lodechosenlatest\UserChoiceLatest\ProgId"))
        {
            choice.SetValue("ProgId", "Lode.Handled");
        }

        using var classes = Registry.CurrentUser.OpenSubKey(scratch.ClassesPath)!;
        using var choices = Registry.CurrentUser.OpenSubKey(choicesPath)!;
        string[] extensions =
        [
            ".lodeitself",
            ".lodequoted",
            ".lodehandled",
            ".lodelibrary",
            ".lodeextension",
            ".lodesystem",
            ".lodechosen",
            ".lodechosenlatest",
            ".lodenothing",
            ""
        ];

        return Verify(extensions.ToDictionary(
            _ => _.Length == 0 ? "(none)" : _,
            _ => ShellIcons.EachFileHasItsOwnIcon(_, classes, choices)))
            .Snapshot(
                """
                {
                  (none): false,
                  .lodechosen: true,
                  .lodechosenlatest: true,
                  .lodeextension: true,
                  .lodehandled: true,
                  .lodeitself: true,
                  .lodelibrary: false,
                  .lodenothing: false,
                  .lodequoted: true,
                  .lodesystem: true
                }
                """);
    }

    // A known folder has its own icon in a folder's list, Downloads' arrow, as Pinned gives it,
    // and any other folder the plain folder's.
    [Test]
    public async Task AKnownFolderHasItsOwnIcon()
    {
        using var fixture = Fixture.Create();
        var downloads = Downloads();

        var icon = ShellIcons.GetIcon(downloads, true);

        await Assert.That(icon).IsEqualTo(ShellIcons.GetPlaceIcon(downloads));
        await Assert.That(icon).IsNotEqualTo(ShellIcons.FolderIcon);
        await Assert.That(ShellIcons.GetIcon(fixture.At("Docs"), true)).IsEqualTo(ShellIcons.FolderIcon);
    }

    /// <summary>
    /// Where the user's Downloads is.
    /// </summary>
    internal static string Downloads()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        Marshal.ThrowExceptionForHR(NativeMethods.SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var path));
        try
        {
            return Marshal.PtrToStringUni(path)!;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
