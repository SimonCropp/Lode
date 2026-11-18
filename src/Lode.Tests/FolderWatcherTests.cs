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

    // For the length of an operation on a folder, the watches at or beneath it let go, so Windows
    // lets the folder be moved. Watches above it, or beside it, stay. Once it is done, those let
    // go report a change, and watch again where the folder is still there.
    [Test]
    public async Task LetsGoBeneathAFolderWhileItIsMoved()
    {
        using var fixture = Fixture.Create();
        var changes = new Dictionary<string, int>
        {
            ["deep"] = 0,
            ["source"] = 0,
            ["docs"] = 0,
            ["root"] = 0
        };
        using var deep = Watch(fixture.At("Source", "Deep"), "deep");
        using var source = Watch(fixture.At("Source"), "source");
        using var docs = Watch(fixture.At("Docs"), "docs");
        using var root = Watch(fixture.Root, "root");

        object whileMoving;
        using (FolderWatcher.LetGo([fixture.At("Source")]))
        {
            Directory.Move(fixture.At("Source"), fixture.At("Archive", "Source"));
            whileMoving = Watching();
        }

        await Verify(
            new
            {
                whileMoving,
                after = Watching(),
                reported = new
                {
                    deep = changes["deep"] > 0,
                    source = changes["source"] > 0
                }
            });

        object Watching() =>
            new
            {
                deep = deep.IsWatching,
                source = source.IsWatching,
                docs = docs.IsWatching,
                root = root.IsWatching
            };

        FolderWatcher Watch(string path, string key) =>
            new(path, NotifyFilters.DirectoryName, () =>
            {
                lock (changes)
                {
                    changes[key]++;
                }
            });
    }

    // A folder still there once the operation is done is watched again, unless its drive is
    // still being removed.
    [Test]
    public async Task WatchesAgainOnceBothTheOperationAndTheDriveAllow()
    {
        using var fixture = Fixture.Create();
        var changes = 0;
        using var watcher = new FolderWatcher(fixture.At("Docs"), NotifyFilters.FileName, () => Interlocked.Increment(ref changes));

        var release = FolderWatcher.LetGo([fixture.Root]);
        var whileLetGo = watcher.IsWatching;
        watcher.DriveChanged(going: true);
        watcher.DriveChanged(going: false);
        var driveBackWhileLetGo = watcher.IsWatching;
        var changesWhileLetGo = changes;
        watcher.DriveChanged(going: true);
        release.Dispose();
        var releasedWhileDriveGoing = watcher.IsWatching;
        watcher.DriveChanged(going: false);

        await Verify(
            new
            {
                whileLetGo,
                driveBackWhileLetGo,
                reportedWhileLetGo = changesWhileLetGo > 0,
                releasedWhileDriveGoing,
                after = watcher.IsWatching,
                reportedAfter = changes > 0
            });
    }

    // A watch on a folder deleted fails, and would report nothing more even once the folder is
    // made again. It lets go when it fails, and the next read revives it, so what changes in the
    // folder made again is heard.
    [Test]
    public async Task WatchesAgainOnceTheFolderIsMadeAgain()
    {
        using var fixture = Fixture.Create();
        var changes = 0;
        var folder = fixture.At("Docs");
        using var watcher = new FolderWatcher(folder, NotifyFilters.FileName | NotifyFilters.DirectoryName, () => Interlocked.Increment(ref changes));

        Directory.Delete(folder, true);
        var letGoOnceDeleted = await Until(() => !watcher.IsWatching);
        Directory.CreateDirectory(folder);
        watcher.Revive();
        var watchingOnceRevived = watcher.IsWatching;
        var changesBefore = changes;
        await File.WriteAllTextAsync(Path.Combine(folder, "after.txt"), "");
        var heardAfter = await Until(() => changes > changesBefore);

        await Assert.That(letGoOnceDeleted).IsTrue();
        await Assert.That(watchingOnceRevived).IsTrue();
        await Assert.That(heardAfter).IsTrue();
    }

    // Reviving a watch that is watching, or that has let go on purpose, changes nothing.
    [Test]
    public async Task ReviveLeavesAWatchAsItIs()
    {
        using var fixture = Fixture.Create();
        using var watcher = new FolderWatcher(fixture.At("Docs"), NotifyFilters.FileName, () => { });

        watcher.Revive();
        var watching = watcher.IsWatching;
        bool whileLetGo;
        using (FolderWatcher.LetGo([fixture.At("Docs")]))
        {
            watcher.Revive();
            whileLetGo = watcher.IsWatching;
        }

        await Assert.That(watching).IsTrue();
        await Assert.That(whileLetGo).IsFalse();
        await Assert.That(watcher.IsWatching).IsTrue();
    }

    static async Task<bool> Until(Func<bool> condition)
    {
        for (var tries = 0; tries < 100; tries++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }
}
