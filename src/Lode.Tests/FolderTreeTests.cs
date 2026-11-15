/// <summary>
/// The tree as text. In place of the machine's drives, This PC holds one "drive": the folder
/// that contains the fixture, so snapshots don't depend on the machine.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class FolderTreeTests
{
    [Test]
    public async Task PinnedFoldersHaveNoChildren()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Docs"), fixture.Root);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task ExpandingLoadsSubfolders()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);

        await Expand(tree, projects);
        await Expand(tree, projects.Nodes[2]);

        await Verify(Dump(tree));
    }

    // A folder is read off the UI thread, so it is not open the moment it is expanded, only
    // once it has been read.
    [Test]
    public async Task ExpandingWaitsForTheFolderToBeRead()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);

        projects.Expand();
        var openAtOnce = projects.IsExpanded;
        await Pump.Loaded(tree);

        await Assert.That(openAtOnce).IsFalse();
        await Assert.That(projects.IsExpanded).IsTrue();
        await Assert.That(Names(projects)).IsEquivalentTo(["Archive", "Docs", "Source"]);
    }

    // The way down to somewhere the tree has not been is read, off the UI thread, before it is
    // selected. Somewhere it has been is selected there and then.
    [Test]
    public async Task SelectPathIsImmediateWhereTheTreeHasBeen()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        var unreadAtOnce = tree.SelectedNode?.Tag as string;
        await Pump.Loaded(tree);
        var unreadOnceRead = tree.SelectedNode?.Tag as string;

        tree.SelectPath(fixture.At("Docs"));
        var readAtOnce = tree.SelectedNode?.Tag as string;

        await Assert.That(unreadAtOnce).IsNotEqualTo(fixture.At("Source", "Deep"));
        await Assert.That(unreadOnceRead).IsEqualTo(fixture.At("Source", "Deep"));
        await Assert.That(readAtOnce).IsEqualTo(fixture.At("Docs"));
    }

    // Only the last folder asked for ends up selected, though an earlier one was still being
    // read its way down to.
    [Test]
    public async Task ALaterSelectPathOvertakesAnEarlierOne()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Assert.That(tree.SelectedNode?.Tag as string).IsEqualTo(fixture.At("Docs"));
    }

    // Picking a folder in the tree itself, while another is still being read its way down to,
    // leaves the one picked selected.
    [Test]
    public async Task PickingAFolderOvertakesOneOnItsWayToBeingSelected()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];

        tree.SelectPath(fixture.At("Source", "Deep"));

        // As the window does when a folder is picked: the tree is told where the view now is.
        tree.SelectedNode = drive;
        tree.SelectPath((string)drive.Tag!);
        await Pump.Loaded(tree);

        await Assert.That(tree.SelectedNode).IsEqualTo(drive);
    }

    [Test]
    public async Task HiddenFoldersWhenShown()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = true });

        await Expand(tree, await Projects(tree));

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathExpandsDownToTheFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree));
    }

    // A pinned folder is a shortcut: the selection goes to the folder under This PC, and the
    // pinned and recent entries for it show as selected as well.
    [Test]
    public async Task SelectPathOfAPinnedFolderSelectsItUnderThisPC()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false }, [fixture.At("Source")], [fixture.At("Source"), fixture.At("Docs")]);

        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree));
    }

    // Moving on to another folder takes the selected look off the pinned entry.
    [Test]
    public async Task SelectingAnotherFolderUnselectsThePinnedEntry()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Source"));
        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);

        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree));
    }

    // Picked by the user, a pinned folder hands the selection on to the tree below.
    [Test]
    public async Task PickingAPinnedFolderMovesToItUnderThisPC()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Source"));
        var selected = new List<string>();
        tree.PathSelected += selected.Add;

        tree.SelectByUser(tree.Nodes[0].Nodes[0]);
        await Pump.Until(() => tree.SelectedNode?.Parent?.Tag is string);

        await Verify(
            new
            {
                selected = selected.Select(fixture.Relative),
                tree = Dump(tree)
            });
    }

    // Outside every drive (a network share, say) the pinned entry itself is selected.
    [Test]
    public async Task SelectPathFallsBackToThePinnedEntry()
    {
        using var fixture = Fixture.Create();
        using var other = Fixture.Create(member: "SelectPathFallsBackToThePinnedEntryOther");
        using var tree = Create(fixture, new() { ShowHidden = false }, other.Root);

        tree.SelectPath(other.Root);
        await Pump.Loaded(tree);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathSettlesForTheParentOfAHiddenFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Cache"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task PinnedAndRecentAreSeparateSections()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false }, [fixture.At("Docs")], [fixture.At("Source")]);

        await Verify(Dump(tree));
    }

    // Pinned and recent folders with the same name, in one section or across the two, are told
    // apart by the folder each is in. One alone in its name keeps its name.
    [Test]
    public async Task FoldersWithTheSameNameAreToldApart()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Deep"));
        Directory.CreateDirectory(fixture.At("Archive", "Deep"));
        using var tree = CreateWithRecent(
            fixture,
            new() { ShowHidden = false },
            [fixture.At("Source", "Deep"), fixture.At("Docs", "Deep"), fixture.At("Docs")],
            [fixture.At("Archive", "Deep")]);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task RecentHiddenWhenTurnedOff()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false, ShowRecent = false }, [fixture.At("Docs")], [fixture.At("Source")]);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task CollapsingASectionIsRemembered()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false };
        using var tree = CreateWithRecent(fixture, settings, [fixture.At("Docs")], [fixture.At("Source")]);

        tree.Nodes[1].Collapse();

        await Assert.That(settings.CollapsedSections).IsEquivalentTo(["Recent"]);
    }

    [Test]
    public async Task ExpandingASectionForgetsItWasCollapsed()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false, CollapsedSections = ["Pinned", "Recent"] };
        using var tree = CreateWithRecent(fixture, settings, [fixture.At("Docs")], [fixture.At("Source")]);

        tree.Nodes[0].Expand();

        await Assert.That(settings.CollapsedSections).IsEquivalentTo(["Recent"]);
    }

    [Test]
    public async Task CollapsedSectionsStartCollapsed()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(
            fixture,
            new() { ShowHidden = false, CollapsedSections = ["Pinned"] },
            [fixture.At("Docs")],
            [fixture.At("Source")]);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task PicksUpFoldersAddedAndRemovedOnDisk()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);
        var projects = await Projects(tree);
        await Expand(tree, projects.Nodes[2]);

        Directory.Delete(fixture.At("Docs"), true);
        Directory.CreateDirectory(fixture.At("Build"));
        await Pump.Until(() => Names(projects).Contains("Build") && !Names(projects).Contains("Docs"));

        await Verify(Dump(tree));
    }

    [Test]
    public async Task ExpandingAgainPicksUpChangesMadeWhileCollapsed()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);
        await Expand(tree, projects);
        projects.Collapse();

        Directory.Delete(fixture.At("Archive"), true);
        await Expand(tree, projects);

        await Verify(Dump(tree));
    }

    // A folder whose only subfolders are hidden has nothing to expand to while hidden folders
    // aren't shown.
    [Test]
    public async Task OnlyHiddenSubfoldersGiveAnExpanderWhenHiddenAreShown()
    {
        using var fixture = Fixture.Create();
        var secret = Directory.CreateDirectory(fixture.At("Docs", "Secret"));
        secret.Attributes |= FileAttributes.Hidden;

        await Assert.That(await DocsHasExpander(fixture, showHidden: false)).IsFalse();
        await Assert.That(await DocsHasExpander(fixture, showHidden: true)).IsTrue();
    }

    static async Task<bool> DocsHasExpander(Fixture fixture, bool showHidden)
    {
        using var tree = Create(fixture, new() { ShowHidden = showHidden });
        var projects = await Projects(tree);
        await Expand(tree, projects);
        var docs = projects.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Docs");
        return docs.Nodes.Count > 0;
    }

    // The selected folder is kept up to date though it is not expanded, so making a folder in
    // an empty one, as New folder in the list does, gives it an expander, and taking that away
    // takes the expander too.
    [Test]
    public async Task SelectedFolderGetsAnExpanderOnceItHasASubfolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);
        var docs = tree.SelectedNode!;
        var expanderAtFirst = docs.Nodes.Count > 0;

        Directory.CreateDirectory(fixture.At("Docs", "Drafts"));
        await Pump.Until(() => Names(docs).Contains("Drafts"));

        Directory.Delete(fixture.At("Docs", "Drafts"));
        await Pump.Until(() => docs.Nodes.Count == 0);

        await Assert.That(expanderAtFirst).IsFalse();
    }

    // Reading an expanded folder again brings the expanders of the folders in it in line with
    // what is in them now.
    [Test]
    public async Task ReadingAFolderAgainUpdatesTheExpandersInIt()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);
        await Expand(tree, projects);

        Directory.CreateDirectory(fixture.At("Archive", "2025"));
        Directory.Delete(fixture.At("Source", "Deep"), true);
        // A change Projects' own watcher sees, which has it read again.
        Directory.CreateDirectory(fixture.At("Build"));
        await Pump.Until(() => Names(projects).Contains("Build"));

        await Verify(Dump(tree));
    }

    static List<string> Names(TreeNode node) =>
        node.Nodes.Cast<TreeNode>().Select(_ => _.Text).ToList();

    // Expands a folder, and waits for it to be read, which the tree does off the UI thread.
    static Task Expand(FolderTree tree, TreeNode node)
    {
        node.Expand();
        return Pump.Loaded(tree);
    }

    // The fixture's Projects folder, under the stand-in drive.
    static async Task<TreeNode> Projects(FolderTree tree)
    {
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];
        await Expand(tree, drive);
        return drive.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Projects");
    }

    static FolderTree Create(Fixture fixture, Settings settings, params string[] pinned) =>
        CreateWithRecent(fixture, settings, pinned, []);

    static FolderTree CreateWithRecent(Fixture fixture, Settings settings, string[] pinned, string[] recent)
    {
        var drive = Path.GetDirectoryName(fixture.Root)!;
        var tree = new FolderTree(settings, new(pinned, recent, ShowComputer: true, _ => "", [drive]));

        // Nodes only expand and select for real once the control has a window.
        _ = tree.Handle;
        return tree;
    }

    static string Dump(FolderTree tree)
    {
        var builder = new StringBuilder();
        Append(builder, tree.Nodes, 0);
        return builder.ToString();
    }

    static void Append(StringBuilder builder, TreeNodeCollection nodes, int depth)
    {
        foreach (TreeNode node in nodes)
        {
            // The stand-in child of a folder that hasn't been expanded yet.
            if (node.Tag is not (string or FolderTree.Section))
            {
                continue;
            }

            var marker = node.Nodes.Count == 0 ? " " : node.IsExpanded ? "-" : "+";
            // Drawn as selected, which a pinned entry for the selected folder is too.
            var selected = node.IsSelected ? "   <- selected" : "";
            builder.Append(' ', depth * 2);
            builder.AppendLine($"{marker} {node.Text}{selected}");
            Append(builder, node.Nodes, depth + 1);
        }
    }
}
