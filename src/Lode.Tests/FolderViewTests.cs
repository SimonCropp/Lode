/// <summary>
/// What the file list shows and selects, as text. These don't depend on how anything is
/// drawn, so they hold on any machine.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class FolderViewTests
{
    const int folderColumn = 1;
    const int dateColumn = 2;
    const int typeColumn = 3;
    const int sizeColumn = 4;

    static readonly Places noPlaces = new([], [], ShowComputer: false, _ => "");

    [Test]
    public Task DisplayNameHidesShortcutExtensions()
    {
        (string Name, bool IsDirectory)[] entries =
        [
            ("DbToMermaid.slnx.lnk", false),
            ("Site.url", false),
            ("UPPER.LNK", false),
            (".lnk", false),
            ("notes.txt", false),
            ("Folder.lnk", true)
        ];
        return Verify(entries.ToDictionary(
            _ => $"{_.Name} (directory: {_.IsDirectory})",
            _ => FolderView.DisplayName(
                new(_.Name, $@"C:\{_.Name}", _.IsDirectory, 0, default, _.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal))));
    }

    // As in Explorer: what the shortcut points at, with the shell's arrow over it.
    [Test]
    public async Task AShortcutShowsTheArrow()
    {
        using var fixture = Fixture.Create();
        var target = fixture.At("Docs", "guide.aleph");
        var shortcut = fixture.At("Docs", "Link to guide.lnk");
        ShellLink.Save(shortcut, target);
        using var view = await Open(fixture.At("Docs"));

        var file = view.FindRow("guide", 0);
        var link = view.FindRow("Link to guide", 0);

        await Assert.That(view.RowIcon(link))
            .IsEqualTo(ShellListView.PaddedIcon(ShellIcons.GetIcon(shortcut, false), ShellIcons.ShortcutOverlay));
        await Assert.That(view.RowIcon(file))
            .IsEqualTo(ShellListView.PaddedIcon(ShellIcons.GetIcon(target, false)));
    }

    [Test]
    public async Task ListsFoldersFirstInNaturalOrder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SortBySizeDescending()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SortByType()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(typeColumn);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SortByDate()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.SortBy(dateColumn);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task FilterByPartOfName()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "REPORT";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    // Everything beneath the folder is searched, all the way down, but for hidden folders.
    [Test]
    public async Task FilterByWildcard()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Cache", "cached.aleph"), "");
        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task SearchFindsInHiddenFoldersWhenShown()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Cache", "cached.aleph"), "");
        using var view = await Open(fixture.Root, new() { ShowHidden = true });

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    // A search shows where each item is, beside its name, and can be sorted by it. Clearing it
    // takes the column away.
    [Test]
    public async Task SearchShowsTheFolderOfEachItem()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);
        var searched = view.ColumnTitles.ToList();
        view.SortBy(folderColumn);
        var sorted = view.Entries.Select(_ => Row(_, view.CurrentPath)).ToList();
        view.SortBy(folderColumn);
        var descending = view.Entries.Select(_ => Row(_, view.CurrentPath)).ToList();
        var cells = view.FirstRowCells.ToList();

        view.Filter = "";

        await Verify(
            new
            {
                searched,
                firstRow = cells,
                sorted,
                descending,
                cleared = view.ColumnTitles
            });
    }

    // A search doesn't go through a junction, which here leads back up to the folder searched.
    [Test]
    public async Task SearchDoesNotFollowLinks()
    {
        using var fixture = Fixture.Create();
        var link = fixture.At("Source", "Loop");
        using (var junction = Process.Start(
                   new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{fixture.Root}\"")
                   {
                       CreateNoWindow = true,
                       UseShellExecute = false
                   })!)
        {
            await junction.WaitForExitAsync();
        }

        using var view = await Open(fixture.Root);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);

        await Verify(
            new
            {
                linked = Directory.Exists(Path.Combine(link, "Docs")),
                rows = view.Entries.Select(_ => Row(_, view.CurrentPath))
            });
    }

    [Test]
    public async Task SearchThatFindsNothing()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.Filter = "nothing";
        var searching = new
        {
            status = view.StatusText,
            emptyText = view.EmptyText
        };
        await Pump.Loaded(view);

        await Verify(
            new
            {
                searching,
                status = view.StatusText,
                emptyText = view.EmptyText
            });
    }

    // On This PC the box narrows the drives, rather than searching every one of them.
    [Test]
    public async Task FilterOnThisPCDoesNotSearch()
    {
        using var view = new FolderView(new(), noPlaces);
        view.Navigate(Locations.ThisPC);
        await Pump.Loaded(view);

        view.Filter = "no drive is called this";
        await Pump.Loaded(view);

        // How many drives there are depends on the machine.
        await Verify(
            new
            {
                narrowed = view.StatusText.StartsWith("0 items (filtered from "),
                emptyText = view.EmptyText,
                columns = view.ColumnTitles
            });
    }

    [Test]
    public async Task FilterIsClearedByNavigating()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Filter = "report";

        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task HiddenItemsWhenShown()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root, new() { ShowHidden = true });

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task History()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var steps = new List<object>();

        async Task Step(string name, Action action)
        {
            action();
            await Pump.Loaded(view);
            steps.Add(
                new
                {
                    step = name,
                    path = fixture.Relative(view.CurrentPath),
                    view.CanGoBack,
                    view.CanGoForward
                });
        }

        await Step("start", () => { });
        await Step("open Source", () => view.Navigate(fixture.At("Source")));
        await Step("open Deep", () => view.Navigate(fixture.At("Source", "Deep")));
        await Step("back", view.GoBack);
        await Step("back", view.GoBack);
        await Step("back at the start does nothing", view.GoBack);
        await Step("forward", view.GoForward);
        await Step("open Docs drops the forward history", () => view.Navigate(fixture.At("Docs")));
        await Step("forward at the end does nothing", view.GoForward);
        await Step("up", view.GoUp);

        await Verify(steps);
    }

    // Going to the folder already shown, by its button in the breadcrumb or Enter in the address
    // bar, reads it again, as in Explorer. What is forward in the history stays, nothing is
    // added to it, and the selection and the filter are kept.
    [Test]
    public async Task GoingWhereItIsAlreadyReadsItAgain()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        view.GoBack();
        await Pump.Loaded(view);
        view.Reveal(fixture.At("Source", "main.aleph"));
        await Pump.Loaded(view);
        view.Filter = "a";
        await Pump.Loaded(view);
        await File.WriteAllBytesAsync(fixture.At("Source", "arrived.aleph"), new byte[3]);

        view.Navigate(fixture.At("source").ToLowerInvariant());
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(view.CanGoForward).IsTrue();
        await Assert.That(Selected(view)).IsEquivalentTo(["main.aleph"]);
        await Assert.That(view.Filter).IsEqualTo("a");
        view.GoBack();
        await Pump.Loaded(view);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
    }

    // Where the view is in its history changes only once it gets there. Sent back to somewhere
    // on the network, then elsewhere before that has been heard from, Back is to the folder it
    // was in, not the one it never got back to.
    [Test]
    public async Task BackOvertakenLeavesTheHistoryAsItWas()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Navigate(fixture.At("Source"));
        await Pump.Loaded(view);
        view.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(view);
        view.IsRemote = _ => Locations.Same(_, fixture.At("Source"));

        view.GoBack();
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(300));
        view.GoBack();
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source", "Deep"));
    }

    [Test]
    public async Task UpSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));

        view.GoUp();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    // As clicking a folder above in the breadcrumb does.
    [Test]
    public async Task GoingToAFolderAboveSelectsTheFolderLeadingBack()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source", "Deep"));

        view.Navigate(fixture.Root);
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task BackToAFolderAboveSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));
        view.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(view);

        view.GoBack();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task RevealSelectsTheFile()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces);

        view.Reveal(fixture.At("report10.beta"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    [Test]
    public async Task PicksUpChangesOnDisk()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);
        File.Delete(fixture.At("summary.gamma"));
        await Pump.Until(() => view.Entries.Any(_ => _.Name == "arrived.aleph") && view.Entries.All(_ => _.Name != "summary.gamma"));

        await Verify(Describe(view, fixture));
    }

    // As in Explorer: the folder deleted from under the view, from Lode or from anywhere else,
    // takes it up to the folder it was in.
    [Test]
    public async Task ADeletedFolderIsLeftForTheFolderItWasIn()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));

        Directory.Delete(fixture.At("Source"), true);
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("Docs");
        await Assert.That(Names(view)).DoesNotContain("Source");
        await Assert.That(view.StatusText).DoesNotContain("find");
    }

    // Deleted and made again at once, as a build emptying its output folder does, the folder
    // stays open, and what changes in it afterwards still shows.
    [Test]
    public async Task AFolderDeletedAndMadeAgainIsStillWatched()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        Directory.Delete(fixture.At("Docs"), true);
        Directory.CreateDirectory(fixture.At("Docs"));
        await Pump.Until(() => !Names(view).Contains("guide.aleph"));
        await File.WriteAllBytesAsync(fixture.At("Docs", "arrived.aleph"), new byte[3]);
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
    }

    // Sent to the Recycle Bin, a folder is moved there, and the view's watch on it goes with it.
    // It is seen to go from the folder it was in.
    [Test]
    public async Task AFolderMovedAwayIsLeftForTheFolderItWasIn()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        Directory.Move(fixture.At("Docs"), fixture.At("Archive", "Docs"));
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Docs");
    }

    // As in Explorer: the folder renamed by another program, the view follows it to its new
    // name, seen from the folder it is in.
    [Test]
    public async Task AFolderRenamedElsewhereIsFollowed()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        Directory.Move(fixture.At("Docs"), fixture.At("Manuals"));
        await Pump.Until(() => view.CurrentPath == fixture.At("Manuals"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // Renamed by Lode, from the tree say, the view follows it, with what was selected still
    // selected, and Back still goes where it went before.
    [Test]
    public async Task AFolderLodeRenamesIsFollowed()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Reveal(fixture.At("Docs", "guide.aleph"));
        await Pump.Loaded(view);

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.Until(() => view.CurrentPath == fixture.At("Documents"));
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));

        view.GoBack();
        await Pump.Loaded(view);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
    }

    // A change of case alone is followed too, so the tab is named as the folder now is.
    [Test]
    public async Task AFolderRenamedToAnotherCaseIsFollowed()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "DOCS");
        await Pump.Until(() => view.CurrentPath == fixture.At("DOCS"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // Moved by Lode, a folder above the one showing takes the view with it.
    [Test]
    public async Task AFolderLodeMovesIsFollowedFromInsideIt()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source", "Deep"));

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("Source")], fixture.At("Archive"), move: true);
        await Pump.Until(() => view.CurrentPath == fixture.At("Archive", "Source", "Deep"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("leaf.aleph");
    }

    // Somewhere in the history that moved is where it went, so Back goes there rather than to
    // somewhere that is no longer there.
    [Test]
    public async Task BackGoesWhereAFolderInTheHistoryWent()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));
        view.GoUp();
        await Pump.Loaded(view);

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.For(TimeSpan.FromMilliseconds(300));
        view.GoBack();
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Documents"));
    }

    // Past the folders above it that have gone too, to the nearest still there.
    [Test]
    public async Task AFolderGoneWithTheOneAboveItIsLeftForTheNearestStillThere()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source", "Deep"));

        Directory.Delete(fixture.At("Source"), true);
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Source");
    }

    // A tab nobody could see when its folder went reads it when it shows, and finding it gone,
    // goes up instead.
    [Test]
    public async Task AFolderThatWentOutOfSightIsLeftWhenTheViewShows()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Showing = false;
        view.Navigate(fixture.At("Docs"));

        Directory.Delete(fixture.At("Docs"), true);
        view.Showing = true;
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Docs");
    }

    [Test]
    public async Task AFolderStillThereIsNotLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));

        await view.LeaveIfGone();

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
    }

    // Whether somewhere on the network is there is asked off the UI thread, since the answer
    // can be a long time coming. The view stays where it is, saying what it is looking for,
    // until it has heard.
    [Test]
    public async Task SomewhereOnTheNetworkIsAskedAfterOffTheUiThread()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.IsRemote = _ => true;

        view.Navigate(fixture.At("Source"));
        var whileAsking = (view.CurrentPath, view.StatusText);
        await Pump.Loaded(view);

        await Assert.That(whileAsking.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(whileAsking.StatusText).IsEqualTo($"Looking for {fixture.At("Source")}...");
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(Names(view)).Contains("main.aleph");
    }

    // Going somewhere else while somewhere on the network is still being asked after leaves
    // the view where it went, whatever is then heard.
    [Test]
    public async Task GoingElsewhereOvertakesSomewhereBeingAskedAfter()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.IsRemote = _ => Locations.Same(_, fixture.At("Source"));

        view.Navigate(fixture.At("Source"));
        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(300));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Docs"));
        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // A tab started somewhere on the network, as one restored when Lode starts is, has its
    // name at once, taken on trust. It asks whether the folder is there when it shows.
    [Test]
    public async Task ATabOnTheNetworkIsTakenOnTrustUntilItShows()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("Source"));
        var atOnce = (view.CurrentPath, Names(view).Count);
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(atOnce).IsEqualTo((fixture.At("Source"), 0));
        await Assert.That(Names(view)).Contains("main.aleph");
    }

    // Started at a file, which means "show me this file", it shows the file's folder with the
    // file selected, once it has found that a file is what it is.
    [Test]
    public async Task ATabStartedAtAFileOnTheNetworkShowsItsFolder()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("report10.beta"));
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
        await Assert.That(view.CanGoBack).IsFalse();
    }

    // Not yet shown, it finds out behind the window that it was started at a file, so its tab
    // is named for the file's folder before it shows, and reads nothing until it does.
    [Test]
    public async Task ATabStartedAtAFileOnTheNetworkIsInItsFolderBeforeItShows()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("report10.beta"));
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        var hidden = Names(view).Count;
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(hidden).IsEqualTo(0);
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
        await Assert.That(view.CanGoBack).IsFalse();
    }

    // Started somewhere that turns out not to be there, a share that is offline say, the tab
    // stays, and says so, rather than Lode waiting on it before it opens.
    [Test]
    public async Task ATabOnTheNetworkThatIsNotThereSaysSo()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Showing = false,
            IsRemote = _ => true
        };

        view.OpenAt(fixture.At("Gone"));
        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.At("Gone"));
        await Assert.That(view.StatusText).IsEqualTo($"Lode can't find '{fixture.At("Gone")}'.");
        await Assert.That(Names(view)).IsEmpty();
    }

    // A folder slow to read is left a while after each read before a change has it read
    // again, rather than being read over and over while something in it keeps changing.
    [Test]
    public async Task ASlowFolderIsNotReadAgainStraightAway()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        // As if the read just finished had taken this long, which leaves it ten times as long.
        view.ReadTook(TimeSpan.FromMilliseconds(300));
        var wait = FolderView.WaitAfter(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);

        // Well past when a folder quick to read would have been read again.
        await Pump.For(TimeSpan.FromSeconds(1.5));
        var readEarly = Names(view).Contains("arrived.aleph");
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(readEarly).IsFalse();
        await Assert.That(watch.Elapsed).IsGreaterThanOrEqualTo(wait - TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task HowLongAFolderIsLeftAfterARead()
    {
        // Quick to read: no longer than changes are already held back.
        await Assert.That(FolderView.WaitAfter(TimeSpan.FromMilliseconds(20))).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(FolderView.WaitAfter(TimeSpan.FromMilliseconds(250))).IsEqualTo(TimeSpan.FromSeconds(2.5));

        // However slow, still read again in the end.
        await Assert.That(FolderView.WaitAfter(TimeSpan.FromSeconds(30))).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    // A view nobody can see, such as a tab in the background, leaves changes on disk unread
    // until it shows again.
    [Test]
    public async Task LeavesChangesUnreadWhileNotShowing()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Showing = false;

        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);

        // Several times as long as a change takes to be read by a view that is showing.
        await Pump.For(TimeSpan.FromSeconds(2));
        var readWhileHidden = Names(view).Contains("arrived.aleph");

        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(readWhileHidden).IsFalse();
        await Assert.That(Names(view)).Contains("arrived.aleph");
    }

    // A folder opened in a view nobody can see, as a tab opened in the background is, is read
    // when the view shows, and the item it was opened for selected then.
    [Test]
    public async Task FolderOpenedWhileNotShowingIsReadWhenShown()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Showing = false
        };

        view.Reveal(fixture.At("report10.beta"));
        await Pump.Loaded(view);
        var path = view.CurrentPath;
        var readWhileHidden = Names(view);

        view.Showing = true;
        await Pump.Loaded(view);

        await Assert.That(path).IsEqualTo(fixture.Root);
        await Assert.That(readWhileHidden).IsEmpty();
        await Assert.That(Names(view)).Contains("report10.beta");
        await Assert.That(Selected(view)).IsEquivalentTo(["report10.beta"]);
    }

    static List<string> Names(FolderView view) =>
        view.Entries.Select(_ => _.Name).ToList();

    // The list holds no rows: it asks for each as it draws it. However many items a folder has,
    // rows are only made for those.
    [Test]
    public async Task MakesOnlyTheRowsItDraws()
    {
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 600);
        using var form = Host();
        var view = await OpenIn(form, folder);

        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new(Point.Empty, form.ClientSize));

        await Assert.That(view.Entries.Count).IsEqualTo(600);
        await Assert.That(view.RowsMade).IsGreaterThan(0);
        await Assert.That(view.RowsMade).IsLessThan(60);
    }

    // Size column to fit goes by every row in the folder, though the list only ever asks for,
    // and so only measures, those on screen.
    [Test]
    public async Task SizeColumnToFitGoesByTheWholeFolder()
    {
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 300);

        // Sorts last, far below the rows on screen.
        var longName = "zzz a name much longer than any other in this folder, and well off the screen.aleph";
        await File.WriteAllBytesAsync(Path.Combine(folder, longName), []);
        using var form = Host();
        var view = await OpenIn(form, folder);

        using var menu = view.HeaderMenu(clickedColumn: 0);
        menu.Items[0].PerformClick();

        var text = TextRenderer.MeasureText(longName, view.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        await Assert.That(view.ColumnWidth(0)).IsGreaterThan(text);

        // The text, its icon and the margins around them, and no more.
        await Assert.That(view.ColumnWidth(0)).IsLessThan(text + 100);
        await Assert.That(view.RowsMade).IsLessThan(60);
    }

    // A double-click on the divider at the right of a column's header sizes it to fit too, and
    // by the whole folder too.
    [Test]
    public async Task DoubleClickingAHeaderDividerFitsByTheWholeFolder()
    {
        const int notifyMessage = 0x004E;
        using var fixture = Fixture.Create();
        var folder = Many(fixture, 300);
        var longName = "zzz a name much longer than any other in this folder, and well off the screen.aleph";
        await File.WriteAllBytesAsync(Path.Combine(folder, longName), []);
        using var form = Host();
        var view = await OpenIn(form, folder);
        var list = view.Controls.OfType<ListView>().Single();
        var header = NativeMethods.SendMessage(list.Handle, NativeMethods.ListViewGetHeader, IntPtr.Zero, IntPtr.Zero);

        // NMHEADER, as the header sends it: itself, an id, the code, then the column.
        var notification = Marshal.AllocHGlobal(6 * IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(notification, header);
            Marshal.WriteIntPtr(notification, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(notification, 2 * IntPtr.Size, NativeMethods.HeaderDividerDoubleClick);
            Marshal.WriteInt32(notification, 3 * IntPtr.Size, 0);
            NativeMethods.SendMessage(list.Handle, notifyMessage, IntPtr.Zero, notification);
        }
        finally
        {
            Marshal.FreeHGlobal(notification);
        }

        var text = TextRenderer.MeasureText(longName, view.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        await Assert.That(view.ColumnWidth(0)).IsGreaterThan(text);
        await Assert.That(view.ColumnWidth(0)).IsLessThan(text + 100);
    }

    // Typing the start of a name moves to the next item that has it, going round to the top.
    [Test]
    public async Task TypingTheStartOfANameFindsTheNextWithIt()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var names = Names(view);

        string? Find(string text, int start)
        {
            var index = view.FindRow(text, start);
            if (index < 0)
            {
                return null;
            }

            return names[index];
        }

        var first = view.FindRow("rep", 0);

        await Assert.That(Find("rep", 0)).IsEqualTo("Report1.beta");
        await Assert.That(Find("REP", first + 1)).IsEqualTo("report2.beta");
        await Assert.That(Find("a", names.Count - 1)).IsEqualTo("Archive");
        await Assert.That(Find("zz", 0)).IsNull();
    }

    // Typed at the list itself, which holds no rows to look through and has the view do it.
    [Test]
    public async Task TypingAtTheListSelectsTheItemFound()
    {
        const int characterMessage = 0x0102;
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.Controls.OfType<ListView>().Single();

        foreach (var character in "rep")
        {
            NativeMethods.SendMessage(list.Handle, characterMessage, character, IntPtr.Zero);
        }

        await Assert.That(Selected(view)).IsEquivalentTo(["Report1.beta"]);
    }

    // The list asks for a renamed row again the moment the edit ends. It has the new name by
    // then, not only once the folder has been read again.
    [Test]
    public async Task RenamingShowsTheNewNameAtOnce()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var index = Names(view).IndexOf("notes.aleph");

        view.OnAfterLabelEdit(null, new(index, "renamed.aleph"));
        var atOnce = view.Entries[index];

        await Assert.That(atOnce.Name).IsEqualTo("renamed.aleph");
        await Assert.That(atOnce.FullPath).IsEqualTo(fixture.At("renamed.aleph"));
        await Assert.That(File.Exists(fixture.At("renamed.aleph"))).IsTrue();
    }

    // The list goes by where a row is, which moves when the folder changes. What was selected
    // stays selected.
    [Test]
    public async Task KeepsTheSelectionWhenTheFolderChanges()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(new() { ShowHidden = false }, noPlaces);
        view.Reveal(fixture.At("report2.beta"));
        await Pump.Loaded(view);

        // Sorts above it, which moves it down a row.
        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[10]);
        await Pump.Until(() => Names(view).Contains("arrived.aleph"));

        await Assert.That(Selected(view)).IsEquivalentTo(["report2.beta"]);
    }

    [Test]
    public async Task SortingKeepsEverythingSelected()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var marquee = view.Marquee;
        marquee.Begin(new(-5, view.RowBounds(0).Top + 2), add: false);
        marquee.Update(new(-5, view.RowBounds(view.Entries.Count - 1).Top + 2));
        marquee.End();
        var selectedBefore = Selected(view).Count;

        view.SortBy(sizeColumn);

        await Assert.That(selectedBefore).IsEqualTo(view.Entries.Count);
        await Assert.That(Selected(view).Count).IsEqualTo(view.Entries.Count);
    }

    // A folder of many files, each with nothing in it.
    static string Many(Fixture fixture, int count)
    {
        var folder = Directory.CreateDirectory(fixture.At("Many")).FullName;
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"file{index}.aleph"), []);
        }

        return folder;
    }

    // A window for a view, off screen. Only in a window does the list draw rows, and measure them.
    static Form Host() =>
        new QuietForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000),
            ClientSize = new(900, 400)
        };

    static async Task<FolderView> OpenIn(Form form, string path)
    {
        var view = new FolderView(new() { ShowHidden = false }, noPlaces)
        {
            Dock = DockStyle.Fill
        };
        form.Controls.Add(view);
        form.Show();
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    // Showing a window normally takes the focus from whatever the developer is doing.
    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [Test]
    public async Task CreateFolder()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));

        view.CreateFolder();
        await Pump.Loaded(view);
        view.CreateFolder();
        await Pump.Loaded(view);

        await Verify(Describe(view, fixture));
    }

    // A drag from the margin left of the list selects the rows it spans.
    [Test]
    public async Task MarqueeFromTheMargin()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var marquee = view.Marquee;

        // Straight down the margin, from the second row to the fourth.
        marquee.Begin(new(-5, view.RowBounds(1).Top + 2), add: false);
        marquee.Update(new(-5, view.RowBounds(3).Top + 2));
        marquee.End();
        var dragged = Selected(view);

        // With Ctrl, the rows covered flip: the fourth is dropped, the fifth added.
        marquee.Begin(new(-5, view.RowBounds(3).Top + 2), add: true);
        marquee.Update(new(-5, view.RowBounds(4).Top + 2));
        marquee.End();
        var toggled = Selected(view);

        // A click in the margin, without Ctrl, clears the selection.
        marquee.Begin(new(-5, view.RowBounds(0).Top + 2), add: false);
        marquee.End();

        await Verify(
            new
            {
                dragged,
                toggled,
                clicked = Selected(view)
            });
    }

    // The horizontal scrollbar runs from the left edge of the view, across the margin, as
    // Explorer's does, rather than from where the list starts. It goes with the list's own:
    // moved, it scrolls the list, and the list scrolled another way, it follows.
    [Test]
    public async Task HorizontalScrollBarRunsAcrossTheMargin()
    {
        const int horizontalScrollMessage = 0x0114;
        const int lineRight = 1;
        const int listViewScroll = 0x1014;
        using var fixture = Fixture.Create();
        using var form = Host();
        form.ClientSize = new(300, 400);
        var view = await OpenIn(form, fixture.Root);
        var list = view.Controls.OfType<ShellListView>().Single();
        var bar = view.ScrollBar;

        await Assert.That(list.Left).IsGreaterThan(0);
        await Assert.That(bar.Visible).IsTrue();
        await Assert.That(bar.Bounds).IsEqualTo(
            Rectangle.FromLTRB(0, list.Top + list.ClientSize.Height, list.Left + list.ClientSize.Width, list.Bottom));
        await Assert.That(bar.Maximum).IsEqualTo(list.HorizontalScrollBar!.Value.Maximum);
        await Assert.That(bar.LargeChange).IsEqualTo(list.HorizontalScrollBar!.Value.Page);

        // A click on its right arrow, which it tells the view about.
        NativeMethods.SendMessage(view.Handle, horizontalScrollMessage, lineRight, bar.Handle);
        var clicked = list.HorizontalScrollBar!.Value.Position;

        await Assert.That(clicked).IsGreaterThan(0);
        await Assert.That(bar.Value).IsEqualTo(clicked);

        // The list scrolled as the keyboard would.
        NativeMethods.SendMessage(list.Handle, listViewScroll, 30, IntPtr.Zero);
        var scrolled = list.HorizontalScrollBar!.Value.Position;

        await Assert.That(scrolled).IsGreaterThan(clicked);
        await Assert.That(bar.Value).IsEqualTo(scrolled);

        // Wide enough for every column, the list has no scrollbar, and so neither has the view.
        form.ClientSize = new(1500, 400);

        await Assert.That(list.HorizontalScrollBar).IsNull();
        await Assert.That(bar.Visible).IsFalse();
    }

    // Hiding a column takes it and its cells out of the list, and sorting by it falls back to
    // the name. Showing it again puts it back in its place.
    [Test]
    public async Task HideAndShowColumns()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false };
        using var view = await Open(fixture.Root, settings);
        view.SortBy(typeColumn);

        view.SetColumnShown(typeColumn, false);
        var hidden = new
        {
            columns = view.ColumnTitles.ToList(),
            firstRow = view.FirstRowCells.ToList(),
            rows = view.Entries.Select(_ => _.Name).ToList(),
            setting = settings.HiddenColumns.ToList()
        };

        view.SetColumnShown(typeColumn, true);

        await Verify(
            new
            {
                hidden,
                shownAgain = new
                {
                    columns = view.ColumnTitles.ToList(),
                    firstRow = view.FirstRowCells.ToList(),
                    setting = settings.HiddenColumns.ToList()
                }
            });
    }

    // Name stays ticked and cannot be unticked.
    [Test]
    public async Task HeaderMenu()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings { ShowHidden = false, HiddenColumns = ["Size"] };
        using var view = await Open(fixture.Root, settings);
        using var menu = view.HeaderMenu(clickedColumn: 1);

        await Verify(
            new
            {
                columns = view.ColumnTitles.ToList(),
                menu = menu.Items.Cast<ToolStripItem>().Select(DescribeMenuItem).ToList()
            });
    }

    static string DescribeMenuItem(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        var text = item.Text!;
        if (item is ToolStripMenuItem { Checked: true })
        {
            text = $"[x] {text}";
        }

        if (!item.Enabled)
        {
            text += " (disabled)";
        }

        return text;
    }

    // A folder Windows marks hidden and system is left out, even with hidden items shown,
    // until system files are shown too.
    [Test]
    public async Task SystemFilesWhenShown()
    {
        using var fixture = Fixture.Create();
        var protectedFolder = Directory.CreateDirectory(fixture.At("Protected"));
        protectedFolder.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        var settings = new Settings { ShowHidden = true };
        using var view = await Open(fixture.Root, settings);
        var withoutSystem = view.Entries.Select(_ => _.Name).ToList();

        settings.ShowSystem = true;
        view.Reload();
        await Pump.Loaded(view);

        await Verify(
            new
            {
                withoutSystem,
                withSystem = view.Entries.Select(_ => _.Name).ToList()
            });
    }

    static List<string> Selected(FolderView view) =>
        view.SelectedEntries().Select(_ => _.Name).ToList();

    static async Task<FolderView> Open(string path, Settings? settings = null)
    {
        // Hidden items off unless a test asks: most tests are about everything else.
        var view = new FolderView(settings ?? new() { ShowHidden = false }, noPlaces);
        view.Navigate(path);
        await Pump.Loaded(view);
        return view;
    }

    static object Describe(FolderView view, Fixture fixture) =>
        new
        {
            path = fixture.Relative(view.CurrentPath),
            status = view.StatusText,
            filter = view.Filter,
            rows = view.Entries.Select(_ => Row(_, view.CurrentPath)),
            selected = view.SelectedEntries().Select(_ => _.Name)
        };

    // Where an item a search found in a subfolder is, from the folder open.
    static string Row(FileEntry entry, string folder)
    {
        var kind = entry.IsDirectory ? "folder" : "file  ";
        var hidden = entry.IsHidden ? "  (hidden)" : "";
        var size = entry.IsDirectory ? "" : $"  {entry.Size:N0} bytes";
        var parent = Path.GetDirectoryName(entry.FullPath)!;
        var where = "";
        if (!string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
        {
            where = $"  in {Path.GetRelativePath(folder, parent)}";
        }

        return $"{kind}  {entry.Name}{size}{hidden}{where}";
    }
}
