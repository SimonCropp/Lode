// Each swaps how the menu is shown.
[NotInParallel("dropMenu")]
public class DropMenuTests
{
    const int rightButton = 2;

    // What Explorer and the list allow a drag of files to do. All is copy, move and scroll.
    const DragDropEffects every = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    static DragEventArgs Drag(string[] paths, DragDropEffects allowed, int keyState = 0) =>
        new(new DataObject(DataFormats.FileDrop, paths), keyState, 0, 0, allowed, DragDropEffects.None);

    // What a drop with the right button asks, the one a left drag would do starred, or null
    // when it asks nothing. Clicking the one named, if any.
    static List<string>? Drop(DragEventArgs drag, string destination, string? click = null)
    {
        List<string>? items = null;
        var showDropMenu = FileOperations.ShowDropMenu;
        FileOperations.ShowDropMenu = menu =>
        {
            using (menu)
            {
                items = menu.Items
                    .Cast<ToolStripItem>()
                    .Select(_ => _ is ToolStripSeparator ? "-" : _.Text + (_.Font.Bold ? "*" : ""))
                    .ToList();
                menu.Items
                    .OfType<ToolStripMenuItem>()
                    .SingleOrDefault(_ => _.Text == click)
                    ?.PerformClick();
            }
        };
        try
        {
            FileOperations.Drop(IntPtr.Zero, drag, destination, rightButton: true);
        }
        finally
        {
            FileOperations.ShowDropMenu = showDropMenu;
        }

        return items;
    }

    // Dropped with the right button, a drag asks what to do, as Explorer's does, offering what
    // the source allows and can be done there, what a left drag would do in bold, and tells the
    // source nothing was moved, for it to delete.
    [Test]
    [STAThreadExecutor]
    public async Task DroppingWithTheRightButtonAsks()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("Docs")];
        var drag = Drag(sources, every);
        var intoArchive = Drop(drag, fixture.At("Archive"));
        var onlyCopy = Drop(Drag(sources, DragDropEffects.Copy), fixture.At("Archive"));
        var withControl = Drop(Drag(sources, every, keyState: 8), fixture.At("Archive"));
        var whereTheyAre = Drop(Drag(sources, every), fixture.Root);
        var ontoItself = Drop(Drag(sources, every), fixture.At("Docs"));

        await Assert.That(drag.Effect).IsEqualTo(DragDropEffects.None);
        await Assert.That(intoArchive).IsEquivalentTo(["Copy here", "Move here*", "Create shortcuts here", "-", "Cancel"], CollectionOrdering.Matching);
        await Assert.That(onlyCopy).IsEquivalentTo(["Copy here*", "-", "Cancel"], CollectionOrdering.Matching);
        await Assert.That(withControl).IsEquivalentTo(["Copy here*", "Move here", "Create shortcuts here", "-", "Cancel"], CollectionOrdering.Matching);
        await Assert.That(whereTheyAre).IsEquivalentTo(["Copy here", "Create shortcuts here", "-", "Cancel"], CollectionOrdering.Matching);
        await Assert.That(ontoItself).IsNull();
    }

    // Dragged with the right button over the folder it is in, where a left drag can't go, it
    // can be dropped, for a copy or shortcut there.
    [Test]
    [STAThreadExecutor]
    public async Task RightDragCanDropWhereItIs()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph")];

        await Assert.That(FileOperations.GetDropEffect(Drag(sources, every), fixture.Root)).IsEqualTo(DragDropEffects.None);
        await Assert.That(FileOperations.GetDropEffect(Drag(sources, every, rightButton), fixture.Root)).IsEqualTo(DragDropEffects.Copy);
        await Assert.That(FileOperations.GetDropEffect(Drag(sources, DragDropEffects.Move | DragDropEffects.Link, rightButton), fixture.Root)).IsEqualTo(DragDropEffects.Link);
    }

    // A folder can't be dropped into itself, nor into a folder inside it, which the shell would
    // refuse: nothing is offered, with either button.
    [Test]
    [STAThreadExecutor]
    public async Task NotIntoAFolderInsideItself()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("Docs")];
        var inside = fixture.At("Docs", "Inside");

        await Assert.That(FileOperations.GetDropEffect(Drag(sources, every), inside)).IsEqualTo(DragDropEffects.None);
        await Assert.That(FileOperations.GetDropEffect(Drag(sources, every, rightButton), inside)).IsEqualTo(DragDropEffects.None);
        await Assert.That(Drop(Drag(sources, every), inside)).IsNull();
    }

    // What is picked from the menu is done: copied, or shortcuts made, there.
    [Test]
    [STAThreadExecutor]
    public async Task WhatIsPickedIsDone()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("Docs")];

        Drop(Drag(sources, every), fixture.At("Archive"), "Create shortcuts here");
        Drop(Drag([fixture.At("notes.aleph")], every), fixture.At("Archive"), "Copy here");
        await Pump.Until(() => File.Exists(fixture.At("Archive", "notes.aleph")));

        await Assert.That(File.Exists(fixture.At("Archive", "notes - Shortcut.lnk"))).IsTrue();
        await Assert.That(File.Exists(fixture.At("Archive", "Docs - Shortcut.lnk"))).IsTrue();
        await Assert.That(File.Exists(fixture.At("notes.aleph"))).IsTrue();
    }
}
