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

    // An app or folder with an ampersand in its name shows it, rather than taking it to mark
    // the next letter as the access key: "Snip & Sketch", not "Snip  Sketch".
    [Test]
    public async Task AnAmpersandInANameShows()
    {
        using var menu = new ContextMenuStrip();
        QuickMenu.AddItems(
            menu.Items,
            [
                new("Snip & Sketch", () => { })
                {
                    Children = [new("R&D", () => { })]
                }
            ],
            _ => { },
            16,
            []);
        var item = (ToolStripMenuItem)menu.Items[0];

        await Assert.That(item.Text).IsEqualTo("Snip && Sketch");
        await Assert.That(item.DropDownItems[0].Text).IsEqualTo("R&&D");
    }

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

    // PowerShell scripts offer Run, after Open, and so do several; a script selected with
    // another file doesn't.
    [Test]
    public async Task ScriptMenu()
    {
        using var fixture = Fixture.Create();
        foreach (var name in new[] {"build.ps1", "clean.PS1"})
        {
            await File.WriteAllTextAsync(fixture.At(name), "");
        }

        using var view = await Open(fixture.Root);

        await Verify(
            new
            {
                script = Describe(view.ItemCommands([Entry(view, "build.ps1")])),
                scripts = Describe(view.ItemCommands([Entry(view, "build.ps1"), Entry(view, "clean.PS1")])),
                scriptAndFile = Describe(view.ItemCommands([Entry(view, "build.ps1"), Entry(view, "notes.aleph")]))
            });
    }

    // Open with's apps are found as it opens, not as the right-click menu is built: building it
    // asks the shell for nothing, opening Open with asks once.
    [Test]
    public async Task OpenWithIsFoundAsItOpens()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var asked = 0;
        view.FindApps = _ =>
        {
            asked++;
            return [new("Firefox", @"C:\Firefox\firefox.exe", 0)];
        };

        using var strip = QuickMenu.Build(view.ItemCommands([Entry(view, "report2.beta")]), null, _ => { });
        var built = asked;
        var openWith = strip.Items.OfType<ToolStripMenuItem>().Single(_ => _.Text == "Open with");
        openWith.ShowDropDown();
        openWith.HideDropDown();
        openWith.ShowDropDown();
        openWith.HideDropDown();

        await Assert.That(built).IsEqualTo(0);
        await Assert.That(asked).IsEqualTo(1);
        await Assert.That(openWith.DropDownItems[0].Text).IsEqualTo("Firefox");
    }

    // The apps Windows recommends, then Choose another app, which takes one file. Nothing for
    // files of different types, programs or shortcuts.
    [Test]
    public async Task OpenWithMenu()
    {
        using var fixture = Fixture.Create();
        foreach (var name in new[] {"todo.aleph", "setup.exe", "Notepad.lnk"})
        {
            await File.WriteAllTextAsync(fixture.At(name), "");
        }

        using var view = await Open(fixture.Root);
        view.FindApps = extension =>
        {
            if (extension == ".beta")
            {
                return [new("Firefox", @"C:\Firefox\firefox.exe", 0), new("GIMP 3.2.6", @"C:\GIMP\gimp.exe", 0)];
            }

            return [];
        };

        List<string> OpenWithEntries(params string[] names)
        {
            var lines = Describe(view.ItemCommands(names.Select(_ => Entry(view, _)).ToList()))
                .SkipWhile(_ => !_.StartsWith("Open with"))
                .TakeWhile(_ => _.StartsWith("Open with") || _.StartsWith("  "))
                .ToList();
            if (lines.Count == 0)
            {
                return ["none"];
            }

            return lines;
        }

        await Verify(
            new
            {
                oneFile = OpenWithEntries("report2.beta"),
                twoFiles = OpenWithEntries("report2.beta", "report10.beta"),
                twoFilesNoApps = OpenWithEntries("notes.aleph", "todo.aleph"),
                oneFileNoApps = OpenWithEntries("notes.aleph"),
                differentTypes = OpenWithEntries("notes.aleph", "report2.beta"),
                program = OpenWithEntries("setup.exe"),
                shortcut = OpenWithEntries("Notepad.lnk")
            });
    }

    [Test]
    public async Task FolderMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "Docs")])));
    }

    // A folder under the code folder gets the git and NuGet commands, and so do several; a
    // file there doesn't, nor does a folder selected with one.
    [Test]
    public async Task FolderUnderTheCodeFolderMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() {ShowHidden = false, CodeDirectory = fixture.Root});

        await Verify(
            new
            {
                folder = Describe(view.ItemCommands([Entry(view, "Docs")])),
                folders = Describe(view.ItemCommands([Entry(view, "Archive"), Entry(view, "Docs")])),
                file = Describe(view.ItemCommands([Entry(view, "notes.aleph")])),
                folderAndFile = Describe(view.ItemCommands([Entry(view, "Docs"), Entry(view, "notes.aleph")])),
                background = Describe(view.BackgroundCommands(canPaste: false, newTypes))
            });
    }

    // Outside the code folder, several folders still get a terminal, a tab for each.
    [Test]
    public async Task FoldersMenu()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view.ItemCommands([Entry(view, "Archive"), Entry(view, "Docs")])));
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
        var settings = new Settings {ShowHidden = false};
        foreach (var id in new[] {"cut", "copy", "rename", "delete", "properties", "createShortcut", "copyAsPath"})
        {
            settings.SetPlacement(id, button: false, menu: false);
        }

        using var view = await Open(fixture.Root, settings);

        await Verify(Describe(LodeActions.ForMenu(view.ItemCommands([Entry(view, "notes.aleph")]), settings)));
    }

    // The menus start with the terminals, the AI actions and the code folder's nested under
    // their parents, each submenu where its first member was.
    [Test]
    public async Task NestedByDefault()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false, CodeDirectory = fixture.Root};
        using var view = await Open(fixture.Root, settings);

        await Verify(
            new
            {
                folder = Describe(LodeActions.ForMenu(view.ItemCommands([Entry(view, "Docs")]), settings)),
                background = Describe(LodeActions.ForMenu(view.BackgroundCommands(canPaste: false, newTypes), settings))
            });
    }

    // Nesting follows Options. With Clipboard's and File's actions nested and the terminals not,
    // File's submenu stands where Pin was, the first of them in this menu, and Rename and Delete
    // join it from further down.
    [Test]
    public async Task NestingFollowsOptions()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        foreach (var id in new[] {"cut", "copy", "copyAsPath", "rename", "delete", "pin", "createShortcut"})
        {
            settings.SetPlacement(id, settings.ShowsAsButton(id), menu: true, menuNested: true);
        }

        foreach (var id in new[] {"terminal", "terminalAdmin"})
        {
            settings.SetPlacement(id, settings.ShowsAsButton(id), menu: true, menuNested: false);
        }

        using var view = await Open(fixture.Root, settings);

        await Verify(Describe(LodeActions.ForMenu(view.ItemCommands([Entry(view, "Docs")]), settings)));
    }

    // The git actions, un-nested, are entries of their own, named for git, as the Git submenu's
    // name no longer says it.
    [Test]
    public async Task GitActionsUnnested()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false, CodeDirectory = fixture.Root};
        foreach (var id in LodeActions.GitIds)
        {
            settings.SetPlacement(id, settings.ShowsAsButton(id), menu: true, menuNested: false);
        }

        using var view = await Open(fixture.Root, settings);

        await Verify(Describe(LodeActions.ForMenu(view.ItemCommands([Entry(view, "Docs")]), settings)));
    }

    [Test]
    public async Task TreeMenu()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateTree([fixture.Root], []);

        await Verify(Describe(tree.NodeCommands(fixture.At("Docs"), newTypes: newTypes)));
    }

    // Without the Claude desktop app or a coding agent, no menu offers Open in Claude or Coding
    // agent, nor does Options.
    [Test]
    public async Task NothingForAppsThatAreNotInstalled()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        using var tree = CreateTree([fixture.Root], []);
        var claude = ClaudeDesktop.IsInstalled;
        var agents = CodingAgents.Installed;
        ClaudeDesktop.IsInstalled = () => false;
        CodingAgents.Installed = () => [];
        try
        {
            List<string> offered =
            [
                .. Describe(view.ItemCommands([Entry(view, "Docs")])),
                .. Describe(view.BackgroundCommands(canPaste: false, newTypes)),
                .. Describe(tree.NodeCommands(fixture.At("Docs"), newTypes: newTypes)),
                .. LodeActions.Offered().Select(_ => _.Text)
            ];

            await Assert.That(offered.Where(_ => _.Contains("Claude") || _.Contains("Coding agent"))).IsEmpty();
        }
        finally
        {
            ClaudeDesktop.IsInstalled = claude;
            CodingAgents.Installed = agents;
        }
    }

    // A drive can't be cut, renamed, deleted or given a shortcut.
    [Test]
    public async Task TreeMenuOnADrive()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateTree([fixture.Root], []);

        await Verify(Describe(tree.NodeCommands(Path.GetPathRoot(fixture.Root)!, canPaste: true, newTypes: newTypes)));
    }

    // A machine on the network has shares rather than folders, so nothing can be made in it.
    [Test]
    public async Task NothingNewOnAMachineOnTheNetwork()
    {
        using var tree = CreateTree([], []);

        var menu = Describe(tree.NodeCommands(@"\\server", newTypes: newTypes));

        await Assert.That(menu).Contains("New (disabled)");
    }

    // New on a folder's right-click menu in the tree opens the folder in the tab, then makes
    // what was chosen there, selected for its name to be edited, as New in the list does.
    [Test]
    public async Task NewInTheTreeMakesItInTheFolder()
    {
        using var fixture = Fixture.Create();
        using var form = await ShowForm(fixture, fixture.Root);
        var view = form.ActiveView!;

        await form.CreateFromTree(fixture.At("Docs"), null);
        await Pump.Loaded(view);
        var folderTab = view.CurrentPath;
        var folderSelected = view.SelectedEntries().Select(_ => _.Name).ToList();
        await form.CreateFromTree(fixture.At("Archive"), new(".txt", "Text Document"));
        await Pump.Loaded(view);

        await Assert.That(folderTab).IsEqualTo(fixture.At("Docs"));
        await Assert.That(folderSelected).IsEquivalentTo(["New folder"]);
        await Assert.That(Directory.Exists(fixture.At("Docs", "New folder"))).IsTrue();
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Archive"));
        await Assert.That(view.SelectedEntries().Select(_ => _.Name)).IsEquivalentTo(["New Text Document.txt"]);
        await Assert.That(File.Exists(fixture.At("Archive", "New Text Document.txt"))).IsTrue();
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
            () =>
            {
            },
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
        var view = new FolderView(settings ?? new() {ShowHidden = false}, noPlaces)
        {
            // What Open with lists depends on what is installed.
            FindApps = _ => []
        };
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    static FileEntry Entry(FolderView view, string name) =>
        view.Entries.Single(_ => _.Name == name);

    // Moving a pinned folder moves the others after it to the end, through the tree's hook,
    // never the real Quick access list, and all in one go.
    [Test]
    public async Task MovePinnedFolder()
    {
        using var fixture = Fixture.Create();
        string[] pinned = [fixture.At("Archive"), fixture.At("Docs"), fixture.At("Source")];
        using var tree = CreateTree(pinned, []);
        var moves = new List<List<string>>();
        tree.MovePinnedToEnd = _ =>
        {
            moves.Add(_.Select(fixture.Relative).ToList());
            return Task.CompletedTask;
        };
        tree.SetPinned = (_, _) => throw new("Moving does not pin or unpin one at a time");

        tree.MovePinned(fixture.At("Source"), 0);

        await Verify(moves);
    }

    // The tree shows the folder in its new place, selected, without waiting for the shell.
    [Test]
    public async Task MovePinnedFolderShownAtOnce()
    {
        using var fixture = Fixture.Create();
        string[] pinned = [fixture.At("Archive"), fixture.At("Docs"), fixture.At("Source")];
        using var tree = CreateTree(pinned, []);
        tree.MovePinnedToEnd = _ => Task.CompletedTask;

        tree.MovePinned(fixture.At("Source"), 0);

        var section = tree.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Pinned");
        var order = section.Nodes.Cast<TreeNode>().Select(_ => (string) _.Tag!).ToList();
        await Assert.That(order).IsEquivalentTo([pinned[2], pinned[0], pinned[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(tree.SelectedNode?.Tag).IsEqualTo(fixture.At("Source"));
    }

    static FolderTree CreateTree(string[] pinned, string[] recent)
    {
        var tree = new FolderTree(
            new()
            {
                ShowHidden = false
            },
            new(pinned, recent, ShowComputer: false, _ => ""))
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
        using var strip = QuickMenu.Build(commands, () =>
        {
        }, _ =>
        {
        });
        var lines = new List<string>();
        Describe(strip.Items, "", lines);
        return lines;
    }

    // A submenu's entries follow it, indented.
    static void Describe(ToolStripItemCollection items, string indent, List<string> lines)
    {
        foreach (ToolStripItem item in items)
        {
            // Opened, as what some hold, Open with's apps say, is found only then.
            if (item is ToolStripMenuItem {HasDropDownItems: true} parent)
            {
                parent.ShowDropDown();
                parent.HideDropDown();
            }

            lines.Add(indent + Describe(item));
            if (item is ToolStripMenuItem {HasDropDownItems: true} opened)
            {
                Describe(opened.DropDownItems, indent + "  ", lines);
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
