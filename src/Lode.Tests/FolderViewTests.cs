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
    const int folderColumn = 2;
    const int dateColumn = 3;
    const int typeColumn = 4;
    const int sizeColumn = 5;

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
        using var view = await Open(fixture.Root, new() {ShowHidden = true});

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
        using var view = await Open(fixture.Root, new() {ShowHidden = true});

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

        await Step("start", () =>
        {
        });
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

    // A Reveal overtaken while its folder is still being asked after selects nothing where the
    // view went instead: going up still lands with the folder left selected.
    [Test]
    public async Task UpAfterAnOvertakenRevealSelectsTheFolderLeft()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Source"));
        view.IsRemote = _ => Locations.Same(_, fixture.At("Docs"));

        view.Reveal(fixture.At("Docs", "guide.aleph"));
        view.GoUp();
        await Pump.Loaded(view);
        await Pump.For(TimeSpan.FromMilliseconds(300));

        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(Selected(view)).IsEquivalentTo(["Source"]);
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

    // A drive refusing to say how big it is, or gone meanwhile, is listed in This PC without
    // its size, rather than making the whole listing an error.
    [Test]
    public async Task ADriveThatCannotBeSizedIsListedWithoutItsSize()
    {
        var refused = FolderView.DriveEntry(@"X:\", () => throw new UnauthorizedAccessException());
        var gone = FolderView.DriveEntry(@"Y:\", () => throw new IOException());
        var sized = FolderView.DriveEntry(@"Z:\", () => (1_000, 400));

        await Assert.That(refused).IsEqualTo(new(@"X:\", @"X:\", true, 0, default, FileAttributes.Directory, IsDrive: true, FreeSpace: 0));
        await Assert.That(gone.Size).IsEqualTo(0);
        await Assert.That((sized.Size, sized.FreeSpace)).IsEqualTo((1_000L, 400L));
    }

    // Delete and Create shortcut chosen from Show more options are done by Lode, as from its own
    // menu, so Ctrl+Z hears of them, rather than by the shell alone.
    [Test]
    public async Task DeleteAndCreateShortcutFromTheClassicMenuAreLodes()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var deleted = new List<string>();
        view.DeleteItems = (_, paths, _) =>
        {
            deleted.AddRange(paths);
            return Task.FromResult(true);
        };
        view.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(view);
        var selection = view.Entries.Where(_ => _.Name == "notes.aleph").ToList();

        var deletes = view.HandleItemVerb("delete", selection);
        var links = view.HandleItemVerb("link", selection);
        await Pump.Loaded(view);

        await Assert.That((deletes, links)).IsEqualTo((true, true));
        await Assert.That(deleted).IsEquivalentTo([fixture.At("notes.aleph")]);
        await Assert.That(File.Exists(fixture.At("notes - Shortcut.lnk"))).IsTrue();
    }

    // New's entries chosen from Show more options are done by Lode, as from its own New, so
    // Ctrl+Z hears of them: by the shell's verbs, NewFolder and a type's extension. A type Lode
    // doesn't know, and Shortcut, a wizard, are left to the shell.
    [Test]
    public async Task NewFromTheClassicMenuIsLodes()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));
        ShellNew.Entry[] types = [new(".txt", "Text Document")];

        var folder = view.HandleBackgroundVerb("NewFolder", types);
        await Pump.Loaded(view);
        var file = view.HandleBackgroundVerb(".txt", types);
        await Pump.Loaded(view);
        var unknown = view.HandleBackgroundVerb(".docx", types);
        var shortcut = view.HandleBackgroundVerb("NewLink", types);

        await Assert.That((folder, file, unknown, shortcut)).IsEqualTo((true, true, false, false));
        await Assert.That(Directory.Exists(fixture.At("Archive", "New folder"))).IsTrue();
        await Assert.That(File.Exists(fixture.At("Archive", "New Text Document.txt"))).IsTrue();
    }

    // Typed in the address bar, what isn't somewhere here is run in the folder open, as Explorer
    // runs cmd, powershell, wt or code there. A folder of that name here, and This PC, are gone
    // to instead.
    [Test]
    public async Task TypedCommandsRunInTheFolder()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("cmd"));
        using var view = await Open(fixture.At("Docs"));
        var started = new List<string>();
        var startTyped = FolderView.StartTyped;
        FolderView.StartTyped = _ => started.Add($"{_.FileName}|{_.Arguments}|{_.WorkingDirectory == fixture.At("Docs")}|{_.UseShellExecute}");
        try
        {
            view.GoToTyped("code .");
            view.GoToTyped("  powershell  ");
            var stayed = view.CurrentPath;
            view.GoToTyped(@"..\cmd");
            await Pump.Loaded(view);
            var folder = view.CurrentPath;
            view.GoToTyped("This PC");
            await Pump.Loaded(view);

            await Assert.That(started).IsEquivalentTo(["code|.|True|True", "powershell||True|True"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(stayed).IsEqualTo(fixture.At("Docs"));
            await Assert.That(folder).IsEqualTo(fixture.At("cmd"));
            await Assert.That(view.CurrentPath).IsEqualTo(Locations.ThisPC);
        }
        finally
        {
            FolderView.StartTyped = startTyped;
        }
    }

    // Run for real, a program is found as Run finds it, on the PATH, and starts in the folder
    // open.
    [Test]
    public async Task ATypedCommandRunsForReal()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Docs"));
        var startTyped = FolderView.StartTyped;
        FolderView.StartTyped = startInfo =>
        {
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            using var process = Process.Start(startInfo)!;
            process.WaitForExit();
        };
        try
        {
            view.GoToTyped("cmd /c cd > here.txt");

            var here = await File.ReadAllTextAsync(fixture.At("Docs", "here.txt"));
            await Assert.That(here.Trim()).IsEqualTo(fixture.At("Docs"));
        }
        finally
        {
            FolderView.StartTyped = startTyped;
        }
    }

    // The shell's names for folders go to where they are on disk.
    [Test]
    public async Task ShellNamesGoToTheirFolders()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);

        view.GoToTyped("shell:SendTo");
        await Pump.Loaded(view);

        await Assert.That(view.CurrentPath).IsEqualTo(Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
    }

    [Test]
    public async Task RevealSelectsTheFile()
    {
        using var fixture = Fixture.Create();
        using var view = new FolderView(
            new()
            {
                ShowHidden = false
            },
            noPlaces);

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
        using var view = new FolderView(
            new()
            {
                ShowHidden = false
            },
            noPlaces)
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
        using var view = new FolderView(
            new()
            {
                ShowHidden = false
            },
            noPlaces)
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
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces)
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
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces)
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

    // A search hands over what it found a quarter of a second at a time, until showing it, all
    // that has been found so far merged in again, comes to take long: then it holds what it
    // finds ten times as long as that took, so the window is not kept busy with it.
    [Test]
    public async Task ASearchHoldsWhatItFindsLongerAsShowingItTakesLonger()
    {
        var pace = new FolderView.SearchPace();
        var first = pace.Hold;
        pace.Shown(TimeSpan.FromMilliseconds(5));
        var quick = pace.Hold;
        pace.Shown(TimeSpan.FromMilliseconds(200));
        var slow = pace.Hold;
        pace.Shown(TimeSpan.FromSeconds(5));
        var slowest = pace.Hold;

        await Assert.That(first).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(quick).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(slow).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(slowest).IsEqualTo(TimeSpan.FromSeconds(10));
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
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces)
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

    // A column sized by dragging its divider, or sized to fit, is that wide in every view from
    // then on, and the next time Lode starts, kept at 100% scale. Hidden and shown again, it is as
    // wide as it was. A width set otherwise, as Windows might set one, is not kept.
    [Test]
    public async Task ColumnWidthsAreKeptForEveryView()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var form = Host();
        var view = await OpenIn(form, fixture.Root, settings);
        var list = view.Controls.OfType<ListView>().Single();
        var sized = new List<FolderView>();
        view.ColumnWidthsChanged += sized.Add;

        // Name, Date modified, Type and Size: Folder only a search has.
        list.Columns[3].Width = 333;
        Header.LetGoOfDivider(list, 1, view.LogicalToDeviceUnits(200));
        using (var menu = view.HeaderMenu(clickedColumn: 0))
        {
            menu.Items[0].PerformClick();
        }

        var nameWidth = (int)Math.Round(view.ColumnWidth(0) * 96.0 / view.DeviceDpi);
        using var other = await Open(fixture.Root, settings);
        var opened = (other.ColumnWidth(0), other.ColumnWidth(1));
        other.SetColumnShown(dateColumn, false);
        other.SetColumnShown(dateColumn, true);

        await Assert.That(settings.ColumnWidths.OrderBy(_ => _.Key).Select(_ => (_.Key, _.Value)))
            .IsEquivalentTo([("Date", 200), ("Name", nameWidth)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sized).IsEquivalentTo([view, view], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(opened).IsEqualTo((other.LogicalToDeviceUnits(nameWidth), other.LogicalToDeviceUnits(200)));
        await Assert.That(other.ColumnWidth(1)).IsEqualTo(other.LogicalToDeviceUnits(200));
    }

    // Each folder is sorted as it was last, in any view, as Explorer keeps each folder's: gone
    // away from and back to, or opened in another view. Other folders are by Name, A to Z. Sorted
    // back to that, a folder is noted no more.
    [Test]
    public async Task EachFolderKeepsItsSort()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.Root, settings);
        view.SortBy(sizeColumn);
        view.SortBy(sizeColumn);

        view.Navigate(fixture.At("Docs"));
        await Pump.Loaded(view);
        var elsewhere = view.Sort;
        view.GoBack();
        await Pump.Loaded(view);
        var back = view.Sort;
        using var other = await Open(fixture.Root, settings);
        var opened = other.Sort;
        var noted = settings.FolderSorts.Select(_ => (_.Column, _.Ascending)).ToList();

        other.SortBy(FolderView.nameColumn);

        await Assert.That(elsewhere).IsEqualTo((FolderView.nameColumn, true));
        await Assert.That(back).IsEqualTo((sizeColumn, false));
        await Assert.That(opened).IsEqualTo((sizeColumn, false));
        await Assert.That(noted).IsEquivalentTo([("Size", false)]);
        await Assert.That(settings.FolderSorts).IsEmpty();
    }

    // Sorted by Folder, a search goes back, once it ends, to how its folder is sorted, which it
    // leaves as it was: Folder is the search's alone.
    [Test]
    public async Task EndingASearchSortedByFolderGoesBackToTheFoldersSort()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.Root, settings);
        view.SortBy(dateColumn);

        view.Filter = "*.aleph";
        await Pump.Loaded(view);
        view.SortBy(folderColumn);
        var searching = view.Sort;
        view.Filter = "";
        await Pump.Loaded(view);

        await Assert.That(searching).IsEqualTo((folderColumn, true));
        await Assert.That(view.Sort).IsEqualTo((dateColumn, true));
        await Assert.That(settings.FolderSorts.Select(_ => (_.Column, _.Ascending))).IsEquivalentTo([("Date", true)]);
    }

    // A folder renamed while a view shows it, the view follows, sorted as it was, and the folder
    // is sorted so under its new name.
    [Test]
    public async Task ARenamedFolderKeepsItsSort()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings {ShowHidden = false};
        using var view = await Open(fixture.At("Docs"), settings);
        view.SortBy(typeColumn);

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.Until(() => view.CurrentPath == fixture.At("Documents"));
        await Pump.Loaded(view);

        await Assert.That(view.Sort).IsEqualTo((typeColumn, true));
        await Assert.That(settings.SortOf(fixture.At("Documents"))?.Column).IsEqualTo("Type");
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
        using var view = new FolderView(new() {ShowHidden = false}, noPlaces);
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

    // A drag over a folder in the list has it highlighted, as Explorer has it, as where the drop
    // would go. Over a file, the drop would go into the folder open, so nothing is.
    [Test]
    public async Task TheFolderADragWouldGoIntoIsHighlighted()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("Archive", "dragged.txt"), "");
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;
        int Index(string name) =>
            view.Entries.ToList().FindIndex(_ => _.Name == name);
        DragEventArgs Over(string name)
        {
            var bounds = list.GetItemRect(Index(name));
            var screen = list.PointToScreen(new(bounds.Left + 20, bounds.Top + bounds.Height / 2));
            return new(new DataObject(DataFormats.FileDrop, new[] { fixture.At("Archive", "dragged.txt") }), 0, screen.X, screen.Y, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None);
        }

        view.OnDragOver(null, Over("Docs"));
        var overFolder = (list.DrawsDropTarget(Index("Docs")), list.DrawsDropTarget(Index("notes.aleph")));
        view.OnDragOver(null, Over("notes.aleph"));
        var overFile = (list.DrawsDropTarget(Index("Docs")), list.DrawsDropTarget(Index("notes.aleph")));

        await Assert.That(overFolder).IsEqualTo((true, false));
        await Assert.That(overFile).IsEqualTo((false, false));
    }

    // What is cut, by Lode or Explorer, is dimmed, as Explorer dims it: the list asks as it draws
    // each row, and is told so for those on the clipboard as cut, and not once they are pasted
    // or something else is copied.
    [Test]
    public async Task CutItemsAreDimmed()
    {
        using var fixture = Fixture.Create();
        using var form = Host();
        var view = await OpenIn(form, fixture.Root);
        var list = view.List;
        int Index(string name) =>
            view.Entries.ToList().FindIndex(_ => _.Name == name);
        byte[] Draw()
        {
            using var bitmap = new Bitmap(list.Width, list.Height);
            list.DrawToBitmap(bitmap, new(Point.Empty, list.Size));
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        try
        {
            var plain = Draw();
            CutItems.Set([fixture.At("notes.aleph"), fixture.At("Docs")]);
            var askedBefore = list.CutAsked;
            var dimmed = Draw();
            var asked = list.CutAsked > askedBefore;
            var cut = (list.DrawsCut(Index("notes.aleph")), list.DrawsCut(Index("Docs")), list.DrawsCut(Index("summary.gamma")));
            CutItems.Set([]);
            var pasted = list.DrawsCut(Index("notes.aleph"));

            await Assert.That(asked).IsTrue();
            await Assert.That(dimmed.SequenceEqual(plain)).IsFalse();
            await Assert.That(cut).IsEqualTo((true, true, false));
            await Assert.That(pasted).IsFalse();
        }
        finally
        {
            CutItems.Set([]);
        }
    }

    // What is pasted is selected once it is there, as Explorer has it: under the name it was
    // given, a copy into its own folder becoming "- Copy", and nothing else that was selected.
    [Test]
    public async Task WhatIsPastedIsSelected()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.At("Archive"));
        await File.WriteAllTextAsync(fixture.At("Archive", "already.aleph"), "");
        view.Reveal(fixture.At("Archive", "already.aleph"));
        await Pump.Loaded(view);

        view.Paste(new([fixture.At("notes.aleph"), fixture.At("Docs")], false));
        await Pump.Loaded(view);
        var intoArchive = Selected(view);

        view.Paste(new([fixture.At("Archive", "notes.aleph")], false));
        await Pump.Loaded(view);

        await Assert.That(intoArchive).IsEquivalentTo(["Docs", "notes.aleph"]);
        await Assert.That(Selected(view)).IsEquivalentTo(["notes - Copy.aleph"]);
    }

    // Ctrl+Shift+C copies what is selected as paths, as in Windows 11's Explorer: quoted, one
    // a line. With nothing selected it copies nothing.
    [Test]
    public async Task CtrlShiftCCopiesAsPath()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        var copied = new List<string>();
        var setClipboardText = FolderView.SetClipboardText;
        FolderView.SetClipboardText = copied.Add;
        try
        {
            var withNothing = view.HandleKey(Keys.Control | Keys.Shift | Keys.C);
            view.Reveal([fixture.At("notes.aleph"), fixture.At("Docs")]);
            await Pump.Loaded(view);
            var handled = view.HandleKey(Keys.Control | Keys.Shift | Keys.C);

            await Assert.That((withNothing, handled)).IsEqualTo((true, true));
            await Assert.That(copied).IsEquivalentTo([$"\"{fixture.At("Docs")}\"{Environment.NewLine}\"{fixture.At("notes.aleph")}\""]);
        }
        finally
        {
            FolderView.SetClipboardText = setClipboardText;
        }
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

    static async Task<FolderView> OpenIn(Form form, string path, Settings? settings = null)
    {
        var view = new FolderView(settings ?? new() {ShowHidden = false}, noPlaces)
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

    static string MakeZip(Fixture fixture, string name, params string[] entries)
    {
        var zip = fixture.At(name);
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var created = archive.CreateEntry(entry);
            if (entry.EndsWith('/'))
            {
                continue;
            }

            using var stream = created.Open();
            stream.Write(new byte[entry.Length]);
        }

        return zip;
    }

    static readonly string[] bundle = ["readme.aleph", "Docs/guide.aleph", "Docs/Deep/", "tool.exe"];

    // A zip lists as a folder: what is at its top, its folders first, those it has no entries
    // of its own for included.
    [Test]
    public async Task ListsAZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(zip);

        await Assert.That(view.InArchive).IsTrue();
        await Verify(Describe(view, fixture));
    }

    // Opened, a zip is gone into, as in Explorer, and so is a folder in it. Up from inside comes
    // back out with what led there selected.
    [Test]
    public async Task GoingIntoAndUpOutOfAZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(fixture.Root);
        view.Reveal(zip);
        await Pump.Loaded(view);

        OpenSelected(view);
        await Pump.Loaded(view);
        var atTop = view.CurrentPath;
        view.Reveal(Path.Combine(zip, "Docs"));
        await Pump.Loaded(view);
        OpenSelected(view);
        await Pump.Loaded(view);
        var inDocs = view.CurrentPath;
        var docsNames = Names(view);
        view.GoUp();
        await Pump.Loaded(view);
        var upToTop = Selected(view);
        view.GoUp();
        await Pump.Loaded(view);

        await Assert.That(atTop).IsEqualTo(zip);
        await Assert.That(inDocs).IsEqualTo(Path.Combine(zip, "Docs"));
        await Assert.That(docsNames).IsEquivalentTo(["Deep", "guide.aleph"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(upToTop).IsEquivalentTo(["Docs"]);
        await Assert.That(view.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(Selected(view)).IsEquivalentTo(["Bundle.zip"]);
    }

    static void OpenSelected(FolderView view) =>
        view.ItemCommands(view.SelectedEntries()).First(_ => _?.Id == "open")!.Action();

    // Typed in the address bar, or reached from the breadcrumb, a folder in a zip opens, and a
    // file in one is shown selected.
    [Test]
    public async Task NavigatingToSomewhereInAZip()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(Path.Combine(zip, "Docs", "Deep"));
        var deep = view.CurrentPath;
        var deepNames = Names(view);

        view.Navigate(Path.Combine(zip, "Docs", "guide.aleph"));
        await Pump.Loaded(view);

        await Assert.That(deep).IsEqualTo(Path.Combine(zip, "Docs", "Deep"));
        await Assert.That(deepNames).IsEmpty();
        await Assert.That(view.CurrentPath).IsEqualTo(Path.Combine(zip, "Docs"));
        await Assert.That(Selected(view)).IsEquivalentTo(["guide.aleph"]);
    }

    // Lode doesn't change a zip: what is in one can be opened, copied and its path copied, and
    // nothing can be made, pasted, dropped, cut, renamed or deleted there. Windows' own menu,
    // which would change it, isn't offered.
    [Test]
    public async Task AZipIsReadOnly()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(zip);
        var deleted = new List<string>();
        view.DeleteItems = (_, paths, _) =>
        {
            deleted.AddRange(paths);
            return Task.FromResult(true);
        };
        view.Reveal([Path.Combine(zip, "Docs"), Path.Combine(zip, "readme.aleph")]);
        await Pump.Loaded(view);

        var itemCommands = view.ItemCommands(view.SelectedEntries()).Select(_ => _?.Id).ToList();
        var backgroundCommands = view.BackgroundCommands(canPaste: true).Select(_ => _?.Id).ToList();
        view.Delete(false);
        view.Paste(new([fixture.At("notes.aleph")], false));
        view.CreateFolder();
        await Pump.Loaded(view);

        await Assert.That(view.IsReadOnly).IsTrue();
        await Assert.That(view.CanShowShellMenu).IsFalse();
        await Assert.That(itemCommands).IsEquivalentTo(["open", null, "copy", "copyAsPath"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(backgroundCommands).IsEquivalentTo(["undo", "redo", null, "extractAll", "copyAsPath"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(deleted).IsEmpty();
        await Assert.That(Names(view)).IsEquivalentTo(["Docs", "readme.aleph", "tool.exe"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // A zip renamed by another program, the view inside it follows it to its new name.
    [Test]
    public async Task AZipRenamedElsewhereIsFollowed()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(Path.Combine(zip, "Docs"));

        File.Move(zip, fixture.At("Renamed.zip"));
        await Pump.Until(() => view.CurrentPath == fixture.At("Renamed.zip", "Docs"));
        await Pump.Loaded(view);

        await Assert.That(Names(view)).Contains("guide.aleph");
    }

    // A zip deleted from under the view takes it to the folder the zip was in.
    [Test]
    public async Task ADeletedZipIsLeftForTheFolderItWasIn()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(Path.Combine(zip, "Docs"));

        File.Delete(zip);
        await Pump.Until(() => view.CurrentPath == fixture.Root);
        await Pump.Loaded(view);

        await Assert.That(Names(view)).DoesNotContain("Bundle.zip");
    }

    // A zip written to while the view is in it is read again.
    [Test]
    public async Task AZipWrittenToIsReadAgain()
    {
        using var fixture = Fixture.Create();
        var zip = MakeZip(fixture, "Bundle.zip", bundle);
        using var view = await Open(zip);

        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
        {
            archive.CreateEntry("arrived.aleph");
        }

        await Pump.Until(() => Names(view).Contains("arrived.aleph"));
    }

    // A folder named as a zip is a folder.
    [Test]
    public async Task AFolderNamedAsAZipIsAFolder()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Folder.zip"));
        await File.WriteAllTextAsync(fixture.At("Folder.zip", "inside.aleph"), "");
        using var view = await Open(fixture.At("Folder.zip"));

        await Assert.That(view.InArchive).IsFalse();
        await Assert.That(Names(view)).IsEquivalentTo(["inside.aleph"]);
    }

    // Compress to ZIP file makes the zip beside what is selected and selects it, ready to rename,
    // as Explorer has it.
    [Test]
    public async Task CompressSelectsTheNewZip()
    {
        using var fixture = Fixture.Create();
        using var view = await Open(fixture.Root);
        view.Reveal([fixture.At("Docs"), fixture.At("notes.aleph")]);
        await Pump.Loaded(view);

        view.Compress(view.SelectedEntries());
        await Pump.Loaded(view);

        await Assert.That(Selected(view)).IsEquivalentTo(["Docs.zip"]);
        ActionLog.Reset();
    }

    // Extract All selects the folder it made.
    [Test]
    public async Task ExtractAllSelectsTheFolder()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Bundle.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("inside.txt");
        }

        using var view = await Open(fixture.Root);

        view.Extract([zip], false);
        await Pump.Loaded(view);

        await Assert.That(Selected(view)).IsEquivalentTo(["Bundle"]);
        ActionLog.Reset();
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
        var settings = new Settings {ShowHidden = false};
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
        var settings = new Settings {ShowHidden = false, HiddenColumns = ["Size"]};
        using var view = await Open(fixture.Root, settings);
        using var menu = view.HeaderMenu(clickedColumn: 1);

        await Verify(
            new
            {
                columns = view.ColumnTitles.ToList(),
                menu = menu.Items.Cast<ToolStripItem>().Select(DescribeMenuItem).ToList()
            });
    }

    // The code folder's repositories each say where they are in a Git column: the branch, then
    // what there is to commit. A folder that isn't one says nothing.
    [Test]
    public async Task GitColumnShowsEachRepository()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        await Assert.That(view.ColumnTitles)
            .IsEquivalentTo(["Name", "Git", "Date modified", "Type", "Size"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(GitCells(view))
            .IsEquivalentTo(["Changed: main · 1 change", "Clean: main", "origins: ", "Plain: "], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(view.GitText).IsEqualTo("");
    }

    // Gone back to, the code folder shows what was last read of each repository at once, while
    // it reads them again, rather than nothing; then what it has read.
    [Test]
    public async Task GoneBackToTheLastGitStatesShowAtOnce()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);
        view.Navigate(code.At("Plain"));
        await Pump.Loaded(view);
        await File.WriteAllTextAsync(code.At("Clean", "tracked.txt"), "changed");
        var release = new ManualResetEventSlim();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            release.Wait(cancel);
            return readGit(repo, cancel);
        };
        try
        {
            view.Navigate(code.Root);
            await Pump.Loaded(view);
            var atOnce = GitCells(view);
            release.Set();
            await GitRead(view);

            await Assert.That(atOnce)
                .IsEquivalentTo(["Changed: main · 1 change", "Clean: main", "origins: ", "Plain: "], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(GitCells(view))
                .IsEquivalentTo(["Changed: main · 1 change", "Clean: main · 1 change", "origins: ", "Plain: "], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            release.Set();
            FolderView.ReadGit = readGit;
            release.Dispose();
        }
    }

    // As TortoiseGit has it, a repository is read again only once something in it changes: the
    // code folder gone back to reads none of them, a change, to a file or by a commit, is read
    // without going anywhere, and Refresh reads them all whatever.
    [Test]
    public async Task RepositoriesAreReadAgainOnlyOnceTheyChange()
    {
        using var code = Repositories();
        var reads = new List<string>();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            lock (reads)
            {
                reads.Add(Path.GetFileName(repo));
            }

            return readGit(repo, cancel);
        };
        try
        {
            using var view = await Open(code.Root, InCode(code));
            await GitRead(view);
            List<string> Reads()
            {
                lock (reads)
                {
                    var taken = reads.Order().ToList();
                    reads.Clear();
                    return taken;
                }
            }

            var first = Reads();
            view.Navigate(code.At("Plain"));
            await Pump.Loaded(view);
            view.Navigate(code.Root);
            await Pump.Loaded(view);
            await GitRead(view);
            var goneBack = Reads();

            await File.WriteAllTextAsync(code.At("Clean", "tracked.txt"), "changed");
            await Pump.Until(() => GitCells(view).Contains("Clean: main · 1 change"));
            await GitRead(view);
            var changed = Reads();

            CodeCommandsTests.Scratch.Git(code.At("Changed"), "commit", "-am", "Change");
            await Pump.Until(() => GitCells(view).Contains("Changed: main · 1 ahead"));
            await GitRead(view);
            var committed = Reads();

            view.ReloadRows();
            await Pump.Loaded(view);
            await GitRead(view);
            var refreshed = Reads();

            await Assert.That(first).IsEquivalentTo(["Changed", "Clean"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(goneBack).IsEmpty();
            await Assert.That(changed).IsEquivalentTo(["Clean"]);
            await Assert.That(committed).IsEquivalentTo(["Changed"]);
            await Assert.That(refreshed).IsEquivalentTo(["Changed", "Clean"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            FolderView.ReadGit = readGit;
        }
    }

    // What a read found is fresh unless something changed in the repository while it was read:
    // that change is then still to be read.
    [Test]
    public async Task AChangeWhileReadingIsStillToBeRead()
    {
        using var code = Repositories();
        var repo = code.At("Clean");
        GitCache.Watch(code.Root);
        var state = new GitState("main", null, 0, 0, []);

        var before = GitCache.Changes(repo);
        GitCache.Store(repo, state, null, before);
        GitCache.TryGet(repo, out _, out _, out var freshAfterRead);
        var during = GitCache.Changes(repo);
        GitCache.MarkStale([repo]);
        GitCache.Store(repo, state, null, during);
        GitCache.TryGet(repo, out _, out _, out var freshAfterChangeDuringRead);

        await Assert.That(freshAfterRead).IsTrue();
        await Assert.That(freshAfterChangeDuringRead).IsFalse();
    }

    // What git writes for itself changes no state, so is no reason to read: an object, and what
    // its fsmonitor daemon makes and deletes for every status it answers, which would otherwise
    // have each read be the reason for the next. Only the repository that has changed is read.
    [Test]
    public async Task WhatGitWritesForItselfIsNotRead()
    {
        using var code = Repositories();
        var cookies = code.At("Clean", ".git", "fsmonitor--daemon", "cookies");
        Directory.CreateDirectory(cookies);
        var reads = new List<string>();
        var readGit = FolderView.ReadGit;
        FolderView.ReadGit = (repo, cancel) =>
        {
            lock (reads)
            {
                reads.Add(Path.GetFileName(repo));
            }

            return readGit(repo, cancel);
        };
        try
        {
            using var view = await Open(code.Root, InCode(code));
            await GitRead(view);

            // Whatever making the repositories left to be heard has been, and read.
            await Pump.For(TimeSpan.FromSeconds(1));
            await GitRead(view);
            lock (reads)
            {
                reads.Clear();
            }

            var cookie = Path.Combine(cookies, "1-1");
            await File.WriteAllTextAsync(cookie, "");
            File.Delete(cookie);
            await File.WriteAllTextAsync(code.At("Clean", ".git", "objects", "info", "object"), "");
            await File.WriteAllTextAsync(code.At("Changed", "new.txt"), "new");
            await Pump.Until(() => GitCells(view).Contains("Changed: main · 2 changes"));
            await GitRead(view);

            List<string> read;
            lock (reads)
            {
                read = reads.ToList();
            }

            await Assert.That(read).IsEquivalentTo(["Changed"]);
        }
        finally
        {
            FolderView.ReadGit = readGit;
        }
    }

    // Too many changes at once for the watch to keep track of, twice over, every repository is
    // stale at once, but the views are told once, when that has settled, not as each comes.
    [Test]
    public async Task TooManyChangesAreToldOfOnceSettled()
    {
        using var code = Repositories();

        // Not a repository, so nothing git left of it is heard meanwhile.
        var repo = code.At("Plain");
        GitCache.Watch(code.Root);
        var told = new List<string>();
        void OnStale(IReadOnlyCollection<string> stale)
        {
            lock (told)
            {
                told.AddRange(stale);
            }
        }

        int Told()
        {
            lock (told)
            {
                return told.Count(_ => _ == repo);
            }
        }

        GitCache.Stale += OnStale;
        try
        {
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
            GitCache.AllChanged();
            GitCache.AllChanged();
            var toldAtOnce = Told();
            GitCache.TryGet(repo, out _, out _, out var fresh);
            await Pump.Until(() => Told() > 0);
            await Pump.For(TimeSpan.FromSeconds(1));

            await Assert.That(toldAtOnce).IsEqualTo(0);
            await Assert.That(fresh).IsFalse();
            await Assert.That(Told()).IsEqualTo(1);
        }
        finally
        {
            GitCache.Stale -= OnStale;
        }
    }

    // A repository whose files go on changing while reading it finds nothing new, a build's
    // output say, is held: it counts as fresh, so nothing reads it. A change of git's own in
    // it, a commit say, ends that, and its views are told once that has settled.
    [Test]
    public async Task HeldWhileNothingNewIsFoundUntilGitItselfChanges()
    {
        using var code = Repositories();

        // Not a repository, so nothing git left of it is heard meanwhile.
        var repo = code.At("Plain");
        var output = Path.Combine(repo, "bin", "output.dll");
        GitCache.Watch(code.Root);
        var told = new List<string>();
        void OnStale(IReadOnlyCollection<string> stale)
        {
            lock (told)
            {
                told.AddRange(stale);
            }
        }

        GitCache.Stale += OnStale;
        try
        {
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
            GitCache.ChangedAt(output);
            GitCache.TryGet(repo, out _, out _, out var freshOnceChanged);
            GitCache.Store(repo, new("main", null, 0, 0, []), null, GitCache.Changes(repo));
            GitCache.ChangedAt(output);
            GitCache.TryGet(repo, out _, out _, out var freshWhileHeld);
            GitCache.ChangedAt(Path.Combine(repo, ".git", "index"));
            GitCache.TryGet(repo, out _, out _, out var freshOnceGitChanged);
            await Pump.Until(
                () =>
                {
                    lock (told)
                    {
                        return told.Contains(repo);
                    }
                });

            await Assert.That(freshOnceChanged).IsFalse();
            await Assert.That(freshWhileHeld).IsTrue();
            await Assert.That(freshOnceGitChanged).IsFalse();
        }
        finally
        {
            GitCache.Stale -= OnStale;
        }
    }

    // By what the column says: nothing first, then by branch, then by what there is to commit.
    [Test]
    public async Task SortByGit()
    {
        using var code = Repositories();
        using var view = await Open(code.Root, InCode(code));
        await GitRead(view);

        view.SortBy(FolderView.gitColumn);

        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["origins", "Plain", "Clean", "Changed"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // The column only shows once the folder has been read and its repositories found, so the
    // sort it was left with is taken up then.
    [Test]
    public async Task AFolderSortedByGitIsSoWhenOpened()
    {
        using var code = Repositories();
        var settings = InCode(code);
        settings.SetSort(code.Root, "Git", false);
        using var view = await Open(code.Root, settings);
        await GitRead(view);

        await Assert.That(view.Sort).IsEqualTo((FolderView.gitColumn, false));
        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["Changed", "Clean", "Plain", "origins"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Hidden from the header's menu, it stays so, but the status bar still says.
    [Test]
    public async Task GitColumnHidden()
    {
        using var code = Repositories();
        var settings = InCode(code);
        settings.HiddenColumns = ["Git"];
        using var view = await Open(code.Root, settings);
        await GitRead(view);
        using var menu = view.HeaderMenu(clickedColumn: 0);

        await Assert.That(view.ColumnTitles).DoesNotContain("Git");
        await Assert.That(menu.Items.Cast<ToolStripItem>().Select(DescribeMenuItem)).Contains("Git");
    }

    // Anywhere in a repository, the status bar says where the repository is.
    [Test]
    public async Task StatusSaysWhereTheRepositoryIs()
    {
        using var code = Repositories();
        var src = code.At("Changed", "src");
        Directory.CreateDirectory(src);
        using var view = await Open(src, InCode(code));
        await GitRead(view);

        await Assert.That(view.GitText).IsEqualTo("main · 1 change");
    }

    // In a repository whose origin is on GitHub, the status bar links to its page there. One
    // whose origin is a folder links nowhere.
    [Test]
    public async Task StatusLinksToTheSite()
    {
        using var code = Repositories();
        CodeCommandsTests.Scratch.Git(code.At("Changed"), "remote", "set-url", "origin", "git@github.com:octo/repo.git");
        using var view = await Open(code.At("Changed"), InCode(code));
        await GitRead(view);
        var onGitHub = view.GitSite;

        view.Navigate(code.At("Clean"));
        await Pump.Loaded(view);
        await GitRead(view);

        await Assert.That(onGitHub).IsEqualTo(new RemoteSite("GitHub", "https://github.com/octo/repo"));
        await Assert.That(view.GitSite).IsNull();
    }

    // In a repository, each file says what has become of it, and each folder how many changes
    // it holds. One unchanged says nothing.
    [Test]
    public async Task GitColumnShowsEachFile()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);

        await Assert.That(view.ColumnTitles)
            .IsEquivalentTo(["Name", "Git", "Date modified", "Type", "Size"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(GitCells(view))
            .IsEquivalentTo(
                [
                    "New: Untracked",
                    "src: 2 changes",
                    "Unchanged: ",
                    "added.txt: Added",
                    "loose.txt: Untracked",
                    "same.txt: ",
                    "tracked.txt: Modified"
                ],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(view.GitText).IsEqualTo("main · 6 changes · 1 ahead");
    }

    // Each in the colour for what has become of it, a folder holding changes as Modified, and
    // one unchanged in the gray of every other detail.
    [Test]
    public async Task GitColumnColoursEachChange()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);

        await Assert.That(GitColor(view, "tracked.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(GitColor(view, "loose.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Untracked));
        await Assert.That(GitColor(view, "added.txt")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Added));
        await Assert.That(GitColor(view, "src")).IsEqualTo(ExplorerStyle.ChangeColor(GitChange.Modified));
        await Assert.That(GitColor(view, "same.txt")).IsEqualTo(ExplorerStyle.SecondaryText);
    }

    // Deeper in, a file is found by its path from the top of the repository.
    [Test]
    public async Task GitColumnInAFolderOfTheRepository()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo", "src"), InCode(code));
        await GitRead(view);

        await Assert.That(GitCells(view))
            .IsEquivalentTo(["Deep: 1 change", "changed.cs: Modified", "same.cs: "], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // A search in a repository says what has become of what it finds in the folders below.
    [Test]
    public async Task GitColumnInASearch()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);

        view.Filter = "*.cs";
        await Pump.Loaded(view);

        await Assert.That(view.ColumnTitles.Take(3))
            .IsEquivalentTo(["Name", "Git", "Folder"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(GitCells(view))
            .IsEquivalentTo(["changed.cs: Modified", "deeper.cs: Modified", "same.cs: "], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Sorted by Git in a repository, what has changed comes together, by what has become of it.
    [Test]
    public async Task SortFilesByGit()
    {
        using var code = Changes();
        using var view = await Open(code.At("Repo"), InCode(code));
        await GitRead(view);

        view.SortBy(FolderView.gitColumn);

        await Assert.That(view.Entries.Select(_ => _.Name))
            .IsEquivalentTo(["Unchanged", "src", "New", "same.txt", "added.txt", "tracked.txt", "loose.txt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // A repository in the code folder with a file changed, one staged as new, one untracked, a
    // folder of them, a folder with changes deeper in, and a file and a folder unchanged.
    static CodeCommandsTests.Scratch Changes()
    {
        var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        Write(repo, "same.txt");
        Write(repo, "Unchanged", "kept.txt");
        Write(repo, "src", "changed.cs");
        Write(repo, "src", "same.cs");
        Write(repo, "src", "Deep", "deeper.cs");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "More");

        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "changed");
        File.WriteAllText(Path.Combine(repo, "src", "changed.cs"), "changed");
        File.WriteAllText(Path.Combine(repo, "src", "Deep", "deeper.cs"), "changed");
        Write(repo, "added.txt");
        CodeCommandsTests.Scratch.Git(repo, "add", "added.txt");
        Write(repo, "loose.txt");
        Write(repo, "New", "one.txt");
        return code;

        static void Write(string repo, params string[] parts)
        {
            var path = Path.Combine([repo, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, parts[^1]);
        }
    }

    // The colour of a row's Git cell, the second column.
    static Color GitColor(FolderView view, string name)
    {
        var index = view.Entries.ToList().FindIndex(_ => _.Name == name);
        return view.CellColor(index, 1);
    }

    // Outside the code folder, as the git actions aren't offered there, nothing is read.
    [Test]
    public async Task NoGitOutsideTheCodeFolder()
    {
        using var code = Repositories();
        using var view = await Open(code.Root);
        await GitRead(view);

        await Assert.That(view.ColumnTitles).DoesNotContain("Git");

        view.Navigate(code.At("Changed"));
        await Pump.Loaded(view);
        await GitRead(view);

        await Assert.That(view.GitText).IsEqualTo("");
    }

    // A clean repository, one with a file changed, and a folder that isn't a repository.
    static CodeCommandsTests.Scratch Repositories()
    {
        var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Clean");
        var changed = code.CreateRepo("Changed");
        File.WriteAllText(Path.Combine(changed, "tracked.txt"), "changed");
        Directory.CreateDirectory(code.At("Plain"));
        return code;
    }

    static Settings InCode(CodeCommandsTests.Scratch code) =>
        new()
        {
            ShowHidden = false,
            CodeDirectory = code.Root
        };

    static async Task GitRead(FolderView view)
    {
        await Pump.Until(() => view.GitReading.IsCompleted);
        await view.GitReading;
    }

    // Each row's name, and what its Git cell says.
    static List<string> GitCells(FolderView view) =>
        view.Entries
            .Select((entry, index) => $"{entry.Name}: {view.RowCells(index)[1]}")
            .ToList();

    static string DescribeMenuItem(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        var text = item.Text!;
        if (item is ToolStripMenuItem {Checked: true})
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
        var settings = new Settings {ShowHidden = true};
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
        var view = new FolderView(settings ?? new() {ShowHidden = false}, noPlaces);
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
