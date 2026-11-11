/// <summary>
/// The fixed right-click menus, and pinning folders to Quick access from the main menu and by
/// dropping on the tree's Pinned header. Pinning itself is swapped out, so the real Quick
/// access list is never changed.
/// </summary>
[TUnit.Core.Executors.STAThreadExecutor]
public class QuickMenuTests
{
    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    [Test]
    public async Task FileMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "notes.aleph")])));
    }

    [Test]
    public async Task FolderMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "Docs")])));
    }

    // No Open in new tab once a file is in the mix, and Rename needs a single item.
    [Test]
    public async Task MixedSelectionMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "Docs"), Entry(view, "notes.aleph")])));
    }

    [Test]
    public async Task BackgroundMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.BackgroundCommands(canPaste: true)));
    }

    [Test]
    public async Task BackgroundMenuWithNothingToPaste()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.BackgroundCommands(canPaste: false)));
    }

    [Test]
    public async Task TreeMenu()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateTree([fixture.Root], []);

        await Verify(Describe(tree.NodeCommands(fixture.At("Docs"))));
    }

    [Test]
    public async Task ClickingHandsOverTheChoice()
    {
        var chosen = new List<string>();
        Action open = () => chosen.Add("open");
        Action showMore = () => chosen.Add("show more");
        Action? handed = null;
        using var strip = QuickMenu.Build([new("Open", open)], showMore, _ => handed = _);

        strip.Items[0].PerformClick();
        handed!();
        strip.Items[^1].PerformClick();
        handed!();

        await Assert.That(chosen).IsEquivalentTo(["open", "show more"]);
    }

    [Test]
    public async Task CopyAsPathQuotesEachPathOnItsOwnLine()
    {
        var text = FolderView.PathsText([@"C:\One", @"C:\Two Words"]);

        await Assert.That(text).IsEqualTo($"\"C:\\One\"{Environment.NewLine}\"C:\\Two Words\"");
    }

    [Test]
    public async Task OnlyExistingUnpinnedFoldersArePinnable()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateTree([fixture.At("Docs")], []);

        var pinnable = tree.Pinnable(
        [
            fixture.At("Docs"),
            fixture.At("Source"),
            fixture.At("notes.aleph"),
            fixture.At("Missing")
        ]);

        await Assert.That(pinnable.Select(fixture.Relative)).IsEquivalentTo(["Projects/Source"]);
    }

    [Test]
    public async Task DroppingOnThePinnedHeaderPins()
    {
        using var fixture = Fixture.Create();
        var pinned = new List<string>();
        using var tree = CreateTree([fixture.At("Docs")], [fixture.At("Archive")]);
        tree.Pin = pinned.Add;

        var handled = tree.PinIfOverPinned(Over(tree, tree.Nodes[0]), fixture.At("Source"));

        await Assert.That(handled).IsTrue();
        await Assert.That(pinned.Select(fixture.Relative)).IsEquivalentTo(["Projects/Source"]);
    }

    [Test]
    public async Task DroppingAPinnedFolderOnThePinnedHeaderDoesNothing()
    {
        using var fixture = Fixture.Create();
        var pinned = new List<string>();
        using var tree = CreateTree([fixture.At("Docs")], []);
        tree.Pin = pinned.Add;

        var handled = tree.PinIfOverPinned(Over(tree, tree.Nodes[0]), fixture.At("Docs"));

        await Assert.That(handled).IsTrue();
        await Assert.That(pinned).IsEmpty();
    }

    // Recent's header is not a place to pin to; the drop is left to the caller.
    [Test]
    public async Task DroppingElsewhereIsNotHandled()
    {
        using var fixture = Fixture.Create();
        var pinned = new List<string>();
        using var tree = CreateTree([fixture.At("Docs")], [fixture.At("Archive")]);
        tree.Pin = pinned.Add;

        var handled = tree.PinIfOverPinned(Over(tree, tree.Nodes[1]), fixture.At("Source"));

        await Assert.That(handled).IsFalse();
        await Assert.That(pinned).IsEmpty();
    }

    [Test]
    public async Task MainMenuOffersUnpinForAPinnedFolder()
    {
        using var fixture = Fixture.Create();
        using var form = await ShowForm(fixture, fixture.Root);

        await Assert.That(form.PinMenuText()).IsEqualTo("Unpin from Quick access");
    }

    [Test]
    public async Task MainMenuOffersPinForAnUnpinnedFolder()
    {
        using var fixture = Fixture.Create();
        using var form = await ShowForm(fixture, fixture.At("Docs"));

        await Assert.That(form.PinMenuText()).IsEqualTo("Pin to Quick access");
    }

    static async Task<FolderView> Open(string path)
    {
        var view = new FolderView(new() { ShowHidden = false }, noPlaces);
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    static FileEntry Entry(FolderView view, string name) =>
        view.Entries.Single(_ => _.Name == name);

    static FolderTree CreateTree(string[] pinned, string[] recent)
    {
        var tree = new FolderTree(new() { ShowHidden = false }, new(pinned, recent, ShowComputer: false, _ => ""))
        {
            Size = new(300, 400)
        };
        _ = tree.Handle;
        return tree;
    }

    static Point Over(FolderTree tree, TreeNode node)
    {
        var bounds = node.Bounds;
        return tree.PointToScreen(new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
    }

    // The fixture's root is the one pinned folder.
    static async Task<MainForm> ShowForm(Fixture fixture, string path)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "");
        var form = new QuietForm([path], new(), places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        return form;
    }

    // What the menu reads as: one line per item, disabled ones marked, separators as dashes.
    static List<string> Describe(IReadOnlyList<QuickMenu.Command?> commands)
    {
        using var strip = QuickMenu.Build(commands, () => { }, _ => { });
        return strip.Items
            .Cast<ToolStripItem>()
            .Select(Describe)
            .ToList();
    }

    static string Describe(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        if (item.Enabled)
        {
            return item.Text!;
        }

        return $"{item.Text} (disabled)";
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
