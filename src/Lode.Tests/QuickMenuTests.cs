/// <summary>
/// The fixed right-click menus, and pinning folders to Quick access from the main menu and by
/// dropping on the tree's Pinned header. Pinning itself is swapped out, so the real Quick
/// access list is never changed.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class QuickMenuTests
{
    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    // In place of this machine's New types, which depend on what is installed.
    static readonly ShellNew.Entry[] newTypes =
    [
        new(".bmp", "Bitmap image"),
        new(".docx", "Microsoft Word Document"),
        new(".txt", "Text Document")
    ];

    [Test]
    public async Task FileMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "notes.aleph")])));
    }

    // Text files offer Edit, after Open.
    [Test]
    public async Task TextFileMenu()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("todo.txt"), "todo");
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "todo.txt")])));
    }

    [Test]
    public async Task FolderMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "Docs")])));
    }

    // A folder under the code folder gets the git and NuGet commands; a file there doesn't.
    [Test]
    public async Task FolderUnderTheCodeFolderMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() { ShowHidden = false, CodeDirectory = fixture.Root });

        await Verify(
            new
            {
                folder = Describe(view.ItemCommands([Entry(view, "Docs")])),
                file = Describe(view.ItemCommands([Entry(view, "notes.aleph")])),
                background = Describe(view.BackgroundCommands(canPaste: false, newTypes))
            });
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

        await Verify(Describe(view.BackgroundCommands(canPaste: true, newTypes)));
    }

    [Test]
    public async Task BackgroundMenuWithNothingToPaste()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.BackgroundCommands(canPaste: false, newTypes)));
    }

    // What Options keeps off the menu is left out, with the separators that would leave
    // doubled or at the end; Show more options too.
    [Test]
    public async Task MenuLeavesOutWhatOptionsHides()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false };
        foreach (var id in new[] { "cut", "copy", "rename", "delete", "properties", "createShortcut", "copyAsPath" })
        {
            settings.SetPlacement(id, button: false, menu: false);
        }

        using var view = await Open(fixture.Root, settings);

        await Verify(Describe(LodeActions.ForMenu(view.ItemCommands([Entry(view, "notes.aleph")]), settings)));
    }

    [Test]
    public async Task TreeMenu()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateTree([fixture.Root], []);

        await Verify(Describe(tree.NodeCommands(fixture.At("Docs"))));
    }

    // A drive can't be cut, renamed, deleted or given a shortcut.
    [Test]
    public async Task TreeMenuOnADrive()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateTree([fixture.Root], []);

        await Verify(Describe(tree.NodeCommands(Path.GetPathRoot(fixture.Root)!, canPaste: true)));
    }

    // Clicking a submenu's own entry only opens it; clicking an entry inside it chooses that.
    [Test]
    public async Task ClickingInASubmenuHandsOverTheChoice()
    {
        var chosen = new List<string>();
        void Pull() => chosen.Add("pull");
        Action? handed = null;
        using var strip = QuickMenu.Build(
            [QuickMenu.Submenu("Git", [new("Pull", Pull)])],
            () => { },
            _ => handed = _);
        var git = (ToolStripMenuItem) strip.Items[0];

        git.PerformClick();
        var handedByParent = handed != null;
        git.DropDownItems[0].PerformClick();
        handed!();

        await Assert.That(handedByParent).IsFalse();
        await Assert.That(chosen).IsEquivalentTo(["pull"]);
    }

    [Test]
    public async Task ClickingHandsOverTheChoice()
    {
        var chosen = new List<string>();
        void Action() => chosen.Add("open");
        void ShowMore() => chosen.Add("show more");
        Action? handed = null;
        using var strip = QuickMenu.Build([new("Open", Action)], ShowMore, _ => handed = _);

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

        await Assert.That(form.PinMenuText()).IsEqualTo("Unpin this folder from Quick access");
    }

    [Test]
    public async Task MainMenuOffersPinForAnUnpinnedFolder()
    {
        using var fixture = Fixture.Create();
        using var form = await ShowForm(fixture, fixture.At("Docs"));

        await Assert.That(form.PinMenuText()).IsEqualTo("Pin this folder to Quick access");
    }

    static async Task<FolderView> Open(string path, Settings? settings = null)
    {
        var view = new FolderView(settings ?? new() { ShowHidden = false }, noPlaces);
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
        var lines = new List<string>();
        Describe(strip.Items, "", lines);
        return lines;
    }

    // A submenu's entries follow it, indented.
    static void Describe(ToolStripItemCollection items, string indent, List<string> lines)
    {
        foreach (ToolStripItem item in items)
        {
            lines.Add(indent + Describe(item));
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                Describe(parent.DropDownItems, indent + "  ", lines);
            }
        }
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
