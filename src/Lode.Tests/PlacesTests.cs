/// <summary>
/// Quick access as remembered from the last start, and read again behind the window. What it
/// reads here is made up, so these hold on any machine.
/// </summary>
[STAThreadExecutor]
// The reading comes back through the UI thread's message queue, as a window's does.
[NotInParallel("windows")]
public class PlacesTests
{
    // The first window has what was remembered, there and then, while Quick access is still
    // being read. Once it has been, the places are what it has now, and so is what is
    // remembered for next time.
    [Test]
    public async Task StartsWithWhatWasRememberedThenReadsAgain()
    {
        var settings = new Settings
        {
            PinnedFolders = [@"C:\Remembered"],
            RecentFolders = [@"C:\Lately"]
        };
        using var release = new ManualResetEventSlim();
        var changes = 0;

        var places = Places.Remembering(
            settings,
            () =>
            {
                release.Wait();
                return ([@"C:\Pinned", @"C:\Also"], [@"C:\Recent"]);
            });
        places.Changed += () => changes++;
        var atOnce = (places.Pinned.ToList(), places.Recent.ToList());

        release.Set();
        await Pump.Until(() => changes > 0);

        await Assert.That(atOnce.Item1).IsEquivalentTo([@"C:\Remembered"]);
        await Assert.That(atOnce.Item2).IsEquivalentTo([@"C:\Lately"]);
        await Assert.That(places.Pinned).IsEquivalentTo([@"C:\Pinned", @"C:\Also"]);
        await Assert.That(places.Recent).IsEquivalentTo([@"C:\Recent"]);
        await Assert.That(settings.PinnedFolders).IsEquivalentTo([@"C:\Pinned", @"C:\Also"]);
        await Assert.That(settings.RecentFolders).IsEquivalentTo([@"C:\Recent"]);
        await Assert.That(changes).IsEqualTo(1);
    }

    // Quick access as it was remembered is nothing to rebuild a tree for. A folder pinned or
    // unpinned is always answered, though: the tree is waiting to hear, changed or not.
    [Test]
    public async Task OnlySaysSoAtTheStartWhenItHasChanged()
    {
        var settings = new Settings
        {
            PinnedFolders = [@"C:\Pinned"]
        };
        var readings = 0;
        var changes = 0;
        var places = Places.Remembering(
            settings,
            () =>
            {
                Interlocked.Increment(ref readings);
                return ([@"C:\Pinned"], []);
            });
        places.Changed += () => changes++;

        // Long enough for the reading to have come back, were it going to say anything.
        await Pump.Until(() => Volatile.Read(ref readings) == 1);
        await Pump.For(TimeSpan.FromMilliseconds(300));
        var afterStart = changes;

        places.Reread();
        await Pump.Until(() => changes > 0);

        await Assert.That(afterStart).IsEqualTo(0);
        await Assert.That(changes).IsEqualTo(1);
    }

    // Two folders pinned one straight after the other: the reading started for the first is
    // overtaken, and only what the last one found is taken.
    [Test]
    public async Task OnlyTheLastReadingCounts()
    {
        var settings = new Settings();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var readings = 0;
        var changes = 0;
        var places = Places.Remembering(
            settings,
            () =>
            {
                // The first reading is the slow one, and comes back last.
                if (Interlocked.Increment(ref readings) == 1)
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                    return ([@"C:\Overtaken"], []);
                }

                return ([@"C:\Latest"], []);
            });
        places.Changed += () => changes++;

        // Each reading has a thread of its own, and nothing says the first asked for is the
        // first to start. This one has, before the next is asked for.
        firstStarted.Wait();
        places.Reread();
        await Pump.Until(() => changes > 0);
        releaseFirst.Set();
        await Pump.For(TimeSpan.FromMilliseconds(300));

        await Assert.That(places.Pinned).IsEquivalentTo([@"C:\Latest"]);
        await Assert.That(changes).IsEqualTo(1);
    }

    // A .zip is a folder in the file system to the shell, so one recently opened on a share,
    // where folders are taken on trust rather than looked for, would be listed as a folder.
    // It is left out, and a real folder is not.
    [Test]
    public async Task AZipIsNotAFolderToList()
    {
        using var fixture = Fixture.Create();
        using (var zip = System.IO.Compression.ZipFile.Open(fixture.At("Archive.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("inside.txt");
        }

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var listed = new List<string>();
        foreach (var item in shell.Namespace(fixture.Root).Items())
        {
            if (Places.FolderToList((object)item, _ => true) is string path)
            {
                listed.Add(Path.GetFileName(path));
            }
        }

        await Assert.That(listed).Contains("Docs");
        await Assert.That(listed).DoesNotContain("Archive.zip");
    }
}
