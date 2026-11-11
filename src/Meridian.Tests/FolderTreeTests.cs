[TUnit.Core.Executors.STAThreadExecutor]
public class FolderTreeTests
{
    [Test]
    public async Task PinnedFoldersAreTheRoots()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new(), fixture.At("Docs"), fixture.Root);

        await Verify(Dump(tree));
    }

    [Test]
    public async Task ExpandingLoadsSubfolders()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new(), fixture.Root);

        tree.Nodes[0].Expand();
        tree.Nodes[0].Nodes[2].Expand();

        await Verify(Dump(tree));
    }

    [Test]
    public async Task HiddenFoldersWhenShown()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new() { ShowHidden = true }, fixture.Root);

        tree.Nodes[0].Expand();

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathExpandsDownToTheFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new(), fixture.Root);

        tree.SelectPath(fixture.At("Source", "Deep"));

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathStartsFromTheClosestPinnedFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new(), fixture.Root, fixture.At("Source"));

        tree.SelectPath(fixture.At("Source", "Deep"));

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathSettlesForTheParentOfAHiddenFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new(), fixture.Root);

        tree.SelectPath(fixture.At("Cache"));

        await Verify(Dump(tree));
    }

    [Test]
    public async Task SelectPathOutsideEveryRootSelectsNothing()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(new(), fixture.At("Docs"));
        tree.SelectPath(fixture.At("Docs"));

        tree.SelectPath(fixture.At("Source"));

        await Verify(Dump(tree));
    }

    static FolderTree Create(Settings settings, params string[] pinned)
    {
        var tree = new FolderTree(settings, new(pinned, ShowComputer: false, _ => ""));

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
            if (node.Tag is not string)
            {
                continue;
            }

            var marker = node.IsExpanded ? "-" : "+";
            var selected = node == tree.SelectedNode ? "   <- selected" : "";
            builder.Append(' ', depth * 2);
            builder.AppendLine($"{marker} {node.Text}{selected}");
            Append(builder, tree, node.Nodes, depth + 1);
        }
    }
}
