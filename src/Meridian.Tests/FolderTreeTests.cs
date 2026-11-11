/// <summary>
/// The tree as text. In place of the machine's drives, This PC holds one "drive": the folder
/// that contains the fixture, so snapshots don't depend on the machine.
/// </summary>
[TUnit.Core.Executors.STAThreadExecutor]
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
        var projects = Projects(tree);

        projects.Expand();
        projects.Nodes[2].Expand();

        await Verify(Dump(tree));
    }

    [Test]
    public async Task HiddenFoldersWhenShown()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = true });

        Projects(tree).Expand();

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathExpandsDownToTheFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));

        await Verify(Dump(tree));
    }

    // A pinned folder is a shortcut: the selection goes to the folder under This PC.
    [Test]
    public async Task SelectPathOfAPinnedFolderSelectsItUnderThisPC()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Source"));

        tree.SelectPath(fixture.At("Source"));

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

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathSettlesForTheParentOfAHiddenFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Cache"));

        await Verify(Dump(tree));
    }

    [Test]
    public async Task PinnedAndRecentAreSeparateSections()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false }, [fixture.At("Docs")], [fixture.At("Source")]);

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
        var projects = Projects(tree);
        projects.Nodes[2].Expand();

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
        var projects = Projects(tree);
        projects.Expand();
        projects.Collapse();

        Directory.Delete(fixture.At("Archive"), true);
        projects.Expand();

        await Verify(Dump(tree));
    }

    static List<string> Names(TreeNode node) =>
        node.Nodes.Cast<TreeNode>().Select(_ => _.Text).ToList();

    // The fixture's Projects folder, under the stand-in drive.
    static TreeNode Projects(FolderTree tree)
    {
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];
        drive.Expand();
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
        Append(builder, tree, tree.Nodes, 0);
        return builder.ToString();
    }

    static void Append(StringBuilder builder, FolderTree tree, TreeNodeCollection nodes, int depth)
    {
        foreach (TreeNode node in nodes)
        {
            // The stand-in child of a folder that hasn't been expanded yet.
            if (node.Tag is not (string or FolderTree.Section))
            {
                continue;
            }

            var marker = node.Nodes.Count == 0 ? " " : node.IsExpanded ? "-" : "+";
            var selected = node == tree.SelectedNode ? "   <- selected" : "";
            builder.Append(' ', depth * 2);
            builder.AppendLine($"{marker} {node.Text}{selected}");
            Append(builder, tree, node.Nodes, depth + 1);
        }
    }
}
