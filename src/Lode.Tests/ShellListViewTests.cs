[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ShellListViewTests
{
    // Every list shows the one set of padded icons. A list that closes, as a tab does, must
    // leave it for the rest.
    [Test]
    public async Task ClosingAListLeavesTheOthersTheirIcons()
    {
        using var kept = new ShellListView();
        _ = kept.Handle;
        var size = ShellListView.PaddedIconSize;

        var closed = new ShellListView();
        _ = closed.Handle;
        closed.Dispose();

        await Assert.That(size.Height).IsGreaterThan(0);
        await Assert.That(ShellListView.PaddedIconSize).IsEqualTo(size);
    }
}
