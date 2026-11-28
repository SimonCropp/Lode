/// <summary>
/// Quick access as remembered from the last start, and read again behind the window. What it
/// reads here is made up, so these hold on any machine.
/// </summary>
[WindowThread]
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
        await using (var zip = await ZipFile.OpenAsync(fixture.At("Archive.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntry("inside.txt");
        }

        var isRemote = Places.IsRemote;
        Places.IsRemote = _ => true;
        var listed = new List<string>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (var item in shell.Namespace(fixture.Root).Items())
            {
                if (Places.FolderToList((object)item) is string path)
                {
                    listed.Add(Path.GetFileName(path));
                }
            }
        }
        finally
        {
            Places.IsRemote = isRemote;
        }

        await Assert.That(listed).Contains("Docs");
        await Assert.That(listed).DoesNotContain("Archive.zip");
    }

    // Asking a drive mapped to a share whether a folder is there waits, when the share can't be
    // reached, off the VPN say, for as long as the network takes to give up, as asking the share
    // does. Quick access is read to pin a folder too, so a folder pinned on one is taken on trust,
    // as one on a share is, and only one on a drive of the machine's own is looked for.
    [Test]
    public async Task AFolderOnAMappedDriveIsTakenOnTrust()
    {
        var isRemote = Places.IsRemote;
        var folderExists = Places.FolderExists;
        var lookedFor = new List<string>();
        Places.IsRemote = _ => Locations.IsNetwork(_) || _.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase);
        Places.FolderExists = _ =>
        {
            lookedFor.Add(_);
            return false;
        };
        string? mapped;
        string? share;
        string? local;
        try
        {
            mapped = Places.FolderToList(new ShellItem(@"Z:\Work"));
            share = Places.FolderToList(new ShellItem(@"\\server\share\Work"));
            local = Places.FolderToList(new ShellItem(@"C:\Gone"));
        }
        finally
        {
            Places.IsRemote = isRemote;
            Places.FolderExists = folderExists;
        }

        await Assert.That(mapped).IsEqualTo(@"Z:\Work");
        await Assert.That(share).IsEqualTo(@"\\server\share\Work");
        await Assert.That(local).IsNull();
        await Assert.That(lookedFor).IsEquivalentTo([@"C:\Gone"]);
    }

    // Pinning reads Quick access, all of it, to see whether the folder is pinned already, and
    // that can wait on the network, so it is done off the UI thread, on one set up for the
    // shell's COM objects, the window going on meanwhile. Pinned shows the folder once it has
    // been pinned and Quick access read again. Nothing here pins anything for real.
    [Test]
    public async Task PinningIsDoneOffTheUIThread()
    {
        var changePinned = ShellContextMenu.ChangePinned;
        var uiThread = Thread.CurrentThread;
        var pinned = new List<string> {@"C:\Before"};
        Thread? pinnedOn = null;
        var apartment = ApartmentState.Unknown;
        ShellContextMenu.ChangePinned = (_, path, _) =>
        {
            pinnedOn = Thread.CurrentThread;
            apartment = Thread.CurrentThread.GetApartmentState();
            lock (pinned)
            {
                pinned.Add(path);
            }
        };
        var places = Places.Remembering(
            new()
            {
                PinnedFolders = [@"C:\Before"]
            },
            () =>
            {
                lock (pinned)
                {
                    return (pinned.ToList(), []);
                }
            });
        ShellContextMenu.QuickAccessChanged += places.Reread;
        try
        {
            var done = ShellContextMenu.SetPinned(IntPtr.Zero, @"C:\Work", true);
            await Pump.Until(() => done.IsCompleted && places.Pinned.Contains(@"C:\Work"));
        }
        finally
        {
            ShellContextMenu.QuickAccessChanged -= places.Reread;
            ShellContextMenu.ChangePinned = changePinned;
        }

        await Assert.That(pinnedOn).IsNotNull();
        await Assert.That(pinnedOn).IsNotEqualTo(uiThread);
        await Assert.That(apartment).IsEqualTo(ApartmentState.STA);
        await Assert.That(places.Pinned).IsEquivalentTo([@"C:\Before", @"C:\Work"], CollectionOrdering.Matching);
    }

    // Folders dropped on Pinned are pinned, then those pinned after where they were dropped
    // moved after them. Each is done on a thread of its own, and they take turns, in the order
    // asked for, each working from what the one before left.
    [Test]
    public async Task PinsTakeTurns()
    {
        var changePinned = ShellContextMenu.ChangePinned;
        var order = new List<string>();
        var running = 0;
        var overlapped = false;
        ShellContextMenu.ChangePinned = (_, path, _) =>
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                overlapped = true;
            }

            // Long enough for another, were it not waiting its turn, to start meanwhile.
            Thread.Sleep(50);
            lock (order)
            {
                order.Add(path);
            }

            Interlocked.Decrement(ref running);
        };
        try
        {
            var pins = new[] {"First", "Second", "Third"}
                .Select(_ => ShellContextMenu.SetPinned(IntPtr.Zero, $@"C:\{_}", true))
                .ToList();
            await Pump.Until(() => pins.All(_ => _.IsCompleted));
        }
        finally
        {
            ShellContextMenu.ChangePinned = changePinned;
        }

        await Assert.That(overlapped).IsFalse();
        await Assert.That(order).IsEquivalentTo([@"C:\First", @"C:\Second", @"C:\Third"], CollectionOrdering.Matching);
    }

    // A window coming to the front reads Quick access again, for what was pinned or used in
    // Explorer or a file dialog meanwhile, but not every time: only once a few minutes have
    // passed since it was last read.
    [Test]
    public async Task ComingToTheFrontReadsAgainOnceAWhileHasPassed()
    {
        var clock = new Clock(new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
        var readings = 0;
        var places = Places.Remembering(
            new(),
            () =>
            {
                Interlocked.Increment(ref readings);
                return ([], []);
            },
            clock);
        await Pump.Until(() => Volatile.Read(ref readings) == 1);

        places.ReadAgainIfStale();
        clock.Now += Places.FreshFor - TimeSpan.FromSeconds(1);
        places.ReadAgainIfStale();
        await Pump.For(TimeSpan.FromMilliseconds(300));
        var tooSoon = Volatile.Read(ref readings);

        clock.Now += TimeSpan.FromSeconds(1);
        places.ReadAgainIfStale();
        await Pump.Until(() => Volatile.Read(ref readings) == 2);
        places.ReadAgainIfStale();
        await Pump.For(TimeSpan.FromMilliseconds(300));

        await Assert.That(tooSoon).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref readings)).IsEqualTo(2);
    }

    // A pinned folder Lode renames, or a recent one in a folder it deletes, has Quick access
    // read again, which follows the folder moved and no longer has the one deleted, off the UI
    // thread. A folder neither pinned nor recent reads nothing.
    [Test]
    public async Task AFolderPinnedOrRecentGoneInLodeIsReadAgain()
    {
        using var fixture = Fixture.Create();
        var uiThread = Thread.CurrentThread;
        var readings = 0;
        var readOn = new List<Thread>();
        var places = Places.Remembering(
            new(),
            () =>
            {
                lock (readOn)
                {
                    readOn.Add(Thread.CurrentThread);
                }

                Interlocked.Increment(ref readings);
                return ([fixture.At("Docs")], [fixture.At("Source", "Deep")]);
            });
        places.FollowedAfter = TimeSpan.Zero;
        await Pump.Until(() => Volatile.Read(ref readings) == 1 && places.Pinned.Count == 1);
        ShellFileOperation.Gone += places.ReadIfGone;
        int neither;
        int renamed;
        try
        {
            await Run(ShellFileOperation.Kind.Rename, fixture.At("Archive"), "Elsewhere");
            await Pump.For(TimeSpan.FromMilliseconds(300));
            neither = Volatile.Read(ref readings);

            await Run(ShellFileOperation.Kind.Rename, fixture.At("Docs"), "Documents");
            await Pump.Until(() => Volatile.Read(ref readings) > neither);
            await Pump.For(TimeSpan.FromMilliseconds(300));
            renamed = Volatile.Read(ref readings);

            await Run(ShellFileOperation.Kind.Delete, fixture.At("Source"), null);
            await Pump.Until(() => Volatile.Read(ref readings) > renamed);
        }
        finally
        {
            ShellFileOperation.Gone -= places.ReadIfGone;
        }

        await Assert.That(neither).IsEqualTo(1);
        await Assert.That(renamed).IsEqualTo(2);
        await Assert.That(Directory.Exists(fixture.At("Documents"))).IsTrue();
        await Assert.That(Directory.Exists(fixture.At("Source"))).IsFalse();
        await Assert.That(readOn).DoesNotContain(uiThread);

        // On a thread of its own, as Lode runs them.
        static Task Run(ShellFileOperation.Kind kind, string source, string? destination) =>
            FileOperations.OnStaThread(() => ShellFileOperation.Run(IntPtr.Zero, kind, [source], destination, ShellFileOperation.NoConfirmation));
    }

    // A shell item as Quick access gives one, by default for a folder in the file system.
    sealed record ShellItem(string Path, bool IsFolder = true, bool IsFileSystem = true, bool IsBrowsable = false);
}
