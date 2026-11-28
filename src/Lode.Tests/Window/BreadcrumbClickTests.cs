/// <summary>
/// The address bar's folders clicked with each button, sent to it as Windows sends clicks, and
/// the menus its chevrons, its ellipsis and a right-click open, shown where they can't be seen.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class BreadcrumbClickTests
{
    const int leftButtonDown = 0x0201;
    const int leftButtonUp = 0x0202;
    const int rightButtonDown = 0x0204;
    const int rightButtonUp = 0x0205;
    const int middleButtonDown = 0x0207;
    const int middleButtonUp = 0x0208;
    const int keyDown = 0x0100;
    const int leftButton = 0x0001;
    const int rightButton = 0x0002;
    const int middleButton = 0x0010;
    const int controlHeld = 0x0008;
    const int shiftHeld = 0x0004;

    // A chevron lists the folders in the folder before it, not its files, by name as the list
    // sorts them, numbers as numbers, each going there when picked. Hidden ones only while
    // hidden items show, and Windows' own only while those do too, as the options are as it
    // opens. An ampersand in a name shows as itself.
    [Test]
    public async Task AChevronListsTheFoldersInTheFolderBeforeIt()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Release 10"));
        Directory.CreateDirectory(fixture.At("Release 9"));
        Directory.CreateDirectory(fixture.At("R&D"));
        Directory.CreateDirectory(fixture.At("Protected"));
        File.SetAttributes(fixture.At("Protected"), FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        var settings = new Settings
        {
            ShowHidden = false
        };
        using var host = new Host(fixture.At("Source", "Deep"), settings);

        var withoutHidden = await Open(host, fixture.Root);
        settings.ShowHidden = true;
        var withHidden = await Open(host, fixture.Root);
        settings.ShowSystem = true;
        var withSystem = await Open(host, fixture.Root);
        var whileOpen = host.Breadcrumb.MenuOpenFor;
        Pick(host.Menus[^1], "Docs");
        await Pump.Until(() => host.Clicked.Count > 0);

        await Verify(
            new
            {
                withoutHidden,
                withHidden,
                withSystem
            })
            .Snapshot(
                """
                {
                  withoutHidden: [
                    Archive,
                    Docs,
                    R&&D,
                    Release 9,
                    Release 10,
                    Source
                  ],
                  withHidden: [
                    Archive,
                    Cache,
                    Docs,
                    R&&D,
                    Release 9,
                    Release 10,
                    Source
                  ],
                  withSystem: [
                    Archive,
                    Cache,
                    Docs,
                    Protected,
                    R&&D,
                    Release 9,
                    Release 10,
                    Source
                  ]
                }
                """);
        await Assert.That(whileOpen).IsEqualTo(fixture.Root);
        await Assert.That(host.Clicked).IsEquivalentTo([fixture.At("Docs")], CollectionOrdering.Matching);
        await Assert.That(host.Breadcrumb.MenuOpenFor).IsNull();
        await Assert.That(host.NewTabs).IsEmpty();
    }

    // The folders are read off the UI thread, the menu saying so until they are: a share or a
    // drive slow to answer leaves the window free meanwhile.
    [Test]
    public async Task AChevronReadsItsFoldersOffTheUIThread()
    {
        using var fixture = Fixture.Create();
        using var host = new Host(fixture.At("Source", "Deep"), new());
        using var gate = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var readOn = -1;
        host.Breadcrumb.ReadFolders = (location, showHidden, hiddenSystem) =>
        {
            readOn = Environment.CurrentManagedThreadId;
            // Held on the UI thread, the click would not come back until the wait gave up.
            gate.Wait(TimeSpan.FromSeconds(20));
            return Breadcrumb.FoldersIn(location, showHidden, hiddenSystem);
        };

        Click(host.Breadcrumb, host.Breadcrumb.ChevronBounds(fixture.At("Source")));
        var whileReading = Describe(host.Menus.Single());
        var heldOpen = host.Breadcrumb.MenuOpenFor;
        gate.Set();
        await Pump.Until(() => Describe(host.Menus.Single()) is not ["Loading… (disabled)"]);

        await Assert.That(whileReading).IsEquivalentTo(["Loading… (disabled)"], CollectionOrdering.Matching);
        await Assert.That(heldOpen).IsEqualTo(fixture.At("Source"));
        await Assert.That(Volatile.Read(ref readOn)).IsNotEqualTo(uiThread);
        await Assert.That(Describe(host.Menus.Single())).IsEquivalentTo(["Deep"], CollectionOrdering.Matching);
    }

    // A folder with no folders in it says so, and one that can't be read says why, as the list
    // would in its place.
    [Test]
    public async Task AChevronSaysWhenThereAreNoFoldersOrTheyCantBeRead()
    {
        using var fixture = Fixture.Create();
        using var empty = new Host(fixture.At("Docs", "Gone"), new());
        using var unreadable = new Host(fixture.At("Gone", "Further"), new());

        await Assert.That(await Open(empty, fixture.At("Docs"))).IsEquivalentTo(["No folders (disabled)"], CollectionOrdering.Matching);
        await Assert.That(await Open(unreadable, fixture.At("Gone"))).IsEquivalentTo(
            [$"Could not find a part of the path '{fixture.At("Gone")}'. (disabled)"],
            CollectionOrdering.Matching);
    }

    // With the bar too narrow for every folder, the ellipsis drops down those it stands for, the
    // nearest first, each going there when picked. The chevron after it lists the folders in
    // the nearest.
    [Test]
    public async Task TheEllipsisListsTheFoldersItStandsFor()
    {
        const string location = @"D:\Alpha folder\Bravo folder\Charlie folder\Delta folder\Six";
        using var host = new Host(location, new(), width: 120);

        Click(host.Breadcrumb, host.Breadcrumb.EllipsisBounds);
        var listed = Describe(host.Menus.Single());
        var whileOpen = host.Breadcrumb.MenuOpenFor;
        Pick(host.Menus.Single(), "Delta folder");
        await Pump.Until(() => host.Clicked.Count > 0);

        await Assert.That(listed).IsEquivalentTo(
            ["Delta folder", "Charlie folder", "Bravo folder", "Alpha folder", "Local Disk (D:)", "This PC"],
            CollectionOrdering.Matching);
        await Assert.That(whileOpen).IsEqualTo("…");
        await Assert.That(host.Clicked).IsEquivalentTo([@"D:\Alpha folder\Bravo folder\Charlie folder\Delta folder"], CollectionOrdering.Matching);
        await Assert.That(host.Breadcrumb.ChevronBounds(@"D:\Alpha folder\Bravo folder\Charlie folder\Delta folder").IsEmpty).IsFalse();
        await Assert.That(host.Breadcrumb.FolderBounds(@"D:\Alpha folder\Bravo folder\Charlie folder\Delta folder").IsEmpty).IsTrue();
    }

    // A folder clicked with the middle button, or with Ctrl held, opens in a new tab, as one in
    // the tree does, and the bar goes nowhere. The middle button let go over another folder
    // than it went down on is no click, Ctrl+Shift is a plain click, and a plain click still
    // goes there.
    [Test]
    public async Task MiddleAndCtrlClickOpenAFolderInANewTab()
    {
        using var fixture = Fixture.Create();
        using var host = new Host(fixture.At("Source", "Deep"), new());
        var projects = Centre(host.Breadcrumb.FolderBounds(fixture.Root));
        var source = Centre(host.Breadcrumb.FolderBounds(fixture.At("Source")));

        MiddleClick(host.Breadcrumb, projects, projects);
        Click(host.Breadcrumb, host.Breadcrumb.FolderBounds(fixture.At("Source")), controlHeld);
        MiddleClick(host.Breadcrumb, projects, source);
        var whileOpening = host.Clicked.ToList();
        Click(host.Breadcrumb, host.Breadcrumb.FolderBounds(fixture.At("Source")), controlHeld | shiftHeld);
        Click(host.Breadcrumb, host.Breadcrumb.FolderBounds(fixture.Root));

        await Assert.That(host.NewTabs).IsEquivalentTo([fixture.Root, fixture.At("Source")], CollectionOrdering.Matching);
        await Assert.That(whileOpening).IsEmpty();
        await Assert.That(host.Clicked).IsEquivalentTo([fixture.At("Source"), fixture.Root], CollectionOrdering.Matching);
        await Assert.That(host.Breadcrumb.Editing).IsFalse();
        await Assert.That(host.Menus).IsEmpty();
    }

    // Right-clicked, a folder offers to copy its path, as Explorer's Copy address does, and to
    // open it in a new tab. A chevron right-clicked offers nothing.
    [Test]
    public async Task RightClickingAFolderOffersCopyAddressAndOpenInNewTab()
    {
        using var fixture = Fixture.Create();
        using var host = new Host(fixture.At("Source", "Deep"), new());
        var copied = new List<string>();
        var setClipboardText = FolderView.SetClipboardText;
        FolderView.SetClipboardText = copied.Add;
        try
        {
            RightClick(host.Breadcrumb, host.Breadcrumb.FolderBounds(fixture.At("Source")));
            var offered = Describe(host.Menus.Single());
            Pick(host.Menus[^1], "Copy address");
            await Pump.Until(() => copied.Count > 0);
            RightClick(host.Breadcrumb, host.Breadcrumb.FolderBounds(fixture.Root));
            Pick(host.Menus[^1], "Open in new tab");
            await Pump.Until(() => host.NewTabs.Count > 0);
            RightClick(host.Breadcrumb, host.Breadcrumb.ChevronBounds(fixture.Root));

            await Assert.That(offered).IsEquivalentTo(["Copy address", "Open in new tab"], CollectionOrdering.Matching);
            await Assert.That(copied).IsEquivalentTo([fixture.At("Source")], CollectionOrdering.Matching);
            await Assert.That(host.NewTabs).IsEquivalentTo([fixture.Root], CollectionOrdering.Matching);
            await Assert.That(host.Menus.Count).IsEqualTo(2);
            await Assert.That(host.Clicked).IsEmpty();
        }
        finally
        {
            FolderView.SetClipboardText = setClipboardText;
        }
    }

    // A chevron's menu takes the keys as it opens, as a menu does: the arrows go down it, Enter
    // goes to the folder picked, and Escape closes it, leaving the chevron as it was. Pressed
    // again while its menu is open, the chevron closes it, rather than opening it again.
    [Test]
    public async Task AChevronsMenuTakesTheKeysAndClosesOnEscape()
    {
        using var fixture = Fixture.Create();
        using var host = new Host(fixture.At("Source", "Deep"), new() { ShowHidden = false });
        var chevron = host.Breadcrumb.ChevronBounds(fixture.Root);
        await Pump.NoModifiersHeld();

        await Open(host, fixture.Root);
        Press(host.Breadcrumb, Keys.Escape);
        await Pump.Until(() => host.Breadcrumb.MenuOpenFor == null);
        var afterEscape = (host.Menus[^1].Visible, host.Clicked.Count);

        await Open(host, fixture.Root);
        Click(host.Breadcrumb, chevron);
        var pressedAgain = (host.Menus.Count, host.Breadcrumb.MenuOpenFor);

        await Open(host, fixture.Root);
        Press(host.Breadcrumb, Keys.Down);
        Press(host.Breadcrumb, Keys.Down);
        Press(host.Breadcrumb, Keys.Enter);
        await Pump.Until(() => host.Clicked.Count > 0);

        await Assert.That(afterEscape).IsEqualTo((false, 0));
        await Assert.That(pressedAgain).IsEqualTo((2, (string?) null));
        await Assert.That(host.Clicked).IsEquivalentTo([fixture.At("Docs")], CollectionOrdering.Matching);
        await Assert.That(host.Breadcrumb.MenuOpenFor).IsNull();
    }

    // Opens the chevron after the folder, and waits for the folders it lists to be read. The menu
    // open before is closed first: pressing the chevron would only close it.
    static async Task<List<string>> Open(Host host, string location)
    {
        foreach (var open in host.Menus)
        {
            open.Close();
        }

        Application.DoEvents();
        var count = host.Menus.Count;
        Click(host.Breadcrumb, host.Breadcrumb.ChevronBounds(location));
        await Pump.Until(() => host.Menus.Count > count);
        var menu = host.Menus[^1];
        await Pump.Until(() => Describe(menu) is not ["Loading… (disabled)"]);
        return Describe(menu);
    }

    static List<string> Describe(ToolStripDropDown menu) =>
        menu.Items
            .Cast<ToolStripItem>()
            .Select(_ => _.Enabled ? _.Text! : $"{_.Text} (disabled)")
            .ToList();

    static void Pick(ToolStripDropDown menu, string text) =>
        menu.Items
            .Cast<ToolStripItem>()
            .Single(_ => _.Text == text)
            .PerformClick();

    static Point Centre(Rectangle bounds) =>
        new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    // Posted, as Windows sends them, so they pass through an open menu on the way, as clicks do.
    static void Click(Control control, Rectangle bounds, int keys = 0)
    {
        var point = Packed(Centre(bounds));
        PostMessage(control.Handle, leftButtonDown, leftButton | keys, point);
        PostMessage(control.Handle, leftButtonUp, keys, point);
        Application.DoEvents();
    }

    static void RightClick(Control control, Rectangle bounds)
    {
        var point = Packed(Centre(bounds));
        PostMessage(control.Handle, rightButtonDown, rightButton, point);
        PostMessage(control.Handle, rightButtonUp, 0, point);
        Application.DoEvents();
    }

    static void MiddleClick(Control control, Point down, Point up)
    {
        PostMessage(control.Handle, middleButtonDown, middleButton, Packed(down));
        PostMessage(control.Handle, middleButtonUp, 0, Packed(up));
        Application.DoEvents();
    }

    // To the window with the focus, as a key pressed goes, which an open menu takes.
    static void Press(Control control, Keys key)
    {
        PostMessage(control.Handle, keyDown, (int) key, 0);
        Application.DoEvents();
    }

    static IntPtr Packed(Point point) =>
        (point.Y << 16) | (point.X & 0xFFFF);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    // A bar for the location, in a window out of sight, its menus shown but transparent, so they
    // take the keys and close as menus do, unseen.
    sealed class Host : IDisposable
    {
        Form form;

        public Breadcrumb Breadcrumb { get; }

        public List<ToolStripDropDown> Menus { get; } = [];

        public List<string> Clicked { get; } = [];

        public List<string> NewTabs { get; } = [];

        public Host(string location, Settings settings, int width = 3000)
        {
            form = new QuietForm
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new(-20000, -20000),
                FormBorderStyle = FormBorderStyle.None
            };
            Breadcrumb = new(settings)
            {
                Width = width,
                Path = location
            };
            Breadcrumb.ShowMenu = (menu, point) =>
            {
                Menus.Add(menu);
                menu.Opacity = 0;
                menu.Show(Breadcrumb, point);
            };
            Breadcrumb.PathClicked += Clicked.Add;
            Breadcrumb.NewTabRequested += NewTabs.Add;
            form.ClientSize = Breadcrumb.Size;
            form.Controls.Add(Breadcrumb);
            form.Show();
            Application.DoEvents();
        }

        public void Dispose()
        {
            foreach (var menu in Menus)
            {
                menu.Dispose();
            }

            form.Dispose();
        }
    }

    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
