/// <summary>
/// A folder watcher letting go of its folder while the drive is being removed, and watching
/// again when that is called off.
/// </summary>
public class FolderWatcherTests
{
    [Test]
    public async Task LetsGoWhileTheDriveIsGoing()
    {
        using var fixture = Fixture.Create();
        var changes = 0;
        using var watcher = new FolderWatcher(fixture.Root, NotifyFilters.FileName, () => Interlocked.Increment(ref changes));
        var watchingAtFirst = watcher.IsWatching;

        watcher.DriveChanged(going: true);
        var watchingWhileGoing = watcher.IsWatching;
        await File.WriteAllTextAsync(fixture.At("while-going.txt"), "");
        await Task.Delay(300);
        var changesWhileGoing = changes;

        watcher.DriveChanged(going: false);
        var watchingAfter = watcher.IsWatching;
        // Called off: reports a change, for whatever happened while it was not watching.
        var changesOnReturn = changes;

        await Verify(
            new
            {
                watchingAtFirst,
                watchingWhileGoing,
                changesWhileGoing,
                watchingAfter,
                reportedOnReturn = changesOnReturn > changesWhileGoing
            });
    }
}
