/// <summary>
/// The tree as text. In place of the machine's drives, This PC holds one "drive": the folder
/// that contains the fixture, so snapshots don't depend on the machine.
/// </summary>
[STAThreadExecutor]
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

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Docs
                    Projects
                - This PC
                  + FolderTreeTests.PinnedFoldersHaveNoChildren

                """);
    }

    [Test]
    public async Task ExpandingLoadsSubfolders()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);

        await Expand(tree, projects);
        await Expand(tree, projects.Nodes[2]);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.ExpandingLoadsSubfolders
                    - Projects
                        Archive
                        Docs
                      - Source
                          Deep

                """);
    }

    // A drive that can't be read, a mapped drive off the network say, keeps its expander, and
    // is read again each time it is expanded, so it opens once it can be read.
    [Test]
    public async Task ADriveThatCantBeReadIsReadAgainWhenExpanded()
    {
        using var fixture = Fixture.Create();
        var offline = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "Offline");
        using var tree = new FolderTree(
            new()
            {
                ShowHidden = false
            },
            new([], [], ShowComputer: true, _ => "", [offline]));
        _ = tree.Handle;
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];

        await Expand(tree, drive);
        var whileOffline = (drive.IsExpanded, drive.Nodes.Count);
        Directory.CreateDirectory(Path.Combine(offline, "Shared"));
        await Expand(tree, drive);

        await Assert.That(whileOffline).IsEqualTo((false, 1));
        await Assert.That(drive.IsExpanded).IsTrue();
        await Assert.That(Names(drive)).IsEquivalentTo(["Shared"]);
    }

    // A folder is read off the UI thread, so it is not open the moment it is expanded, only
    // once it has been read.
    [Test]
    public async Task ExpandingWaitsForTheFolderToBeRead()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);

        projects.Expand();
        var openAtOnce = projects.IsExpanded;
        await Pump.Loaded(tree);

        await Assert.That(openAtOnce).IsFalse();
        await Assert.That(projects.IsExpanded).IsTrue();
        await Assert.That(Names(projects)).IsEquivalentTo(["Archive", "Docs", "Source"]);
    }

    // The way down to somewhere the tree has not been is read, off the UI thread, before it is
    // selected. Somewhere it has been is selected there and then.
    [Test]
    public async Task SelectPathIsImmediateWhereTheTreeHasBeen()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        var unreadAtOnce = tree.SelectedNode?.Tag as string;
        await Pump.Loaded(tree);
        var unreadOnceRead = tree.SelectedNode?.Tag as string;

        tree.SelectPath(fixture.At("Docs"));
        var readAtOnce = tree.SelectedNode?.Tag as string;

        await Assert.That(unreadAtOnce).IsNotEqualTo(fixture.At("Source", "Deep"));
        await Assert.That(unreadOnceRead).IsEqualTo(fixture.At("Source", "Deep"));
        await Assert.That(readAtOnce).IsEqualTo(fixture.At("Docs"));
    }

    // Only the last folder asked for ends up selected, though an earlier one was still being
    // read its way down to.
    [Test]
    public async Task ALaterSelectPathOvertakesAnEarlierOne()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Assert.That(tree.SelectedNode?.Tag as string).IsEqualTo(fixture.At("Docs"));
    }

    // Picking a folder in the tree itself, while another is still being read its way down to,
    // leaves the one picked selected.
    [Test]
    public async Task PickingAFolderOvertakesOneOnItsWayToBeingSelected()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];

        tree.SelectPath(fixture.At("Source", "Deep"));

        // As the window does when a folder is picked: the tree is told where the view now is.
        tree.SelectedNode = drive;
        tree.SelectPath((string)drive.Tag!);
        await Pump.Loaded(tree);

        await Assert.That(tree.SelectedNode).IsEqualTo(drive);
    }

    [Test]
    public async Task HiddenFoldersWhenShown()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = true });

        await Expand(tree, await Projects(tree));

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.HiddenFoldersWhenShown
                    - Projects
                        Archive
                        Cache
                        Docs
                      + Source

                """);
    }

    [Test]
    public async Task SelectPathExpandsDownToTheFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.SelectPathExpandsDownToTheFolder
                    - Projects
                        Archive
                        Docs
                      - Source
                          Deep   <- selected

                """);
    }

    // A pinned folder is a shortcut: the selection goes to the folder under This PC, and the
    // pinned and recent entries for it show as selected as well.
    [Test]
    public async Task SelectPathOfAPinnedFolderSelectsItUnderThisPC()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false }, [fixture.At("Source")], [fixture.At("Source"), fixture.At("Docs")]);

        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Source   <- selected
                - Recent
                    Source   <- selected
                    Docs
                - This PC
                  - FolderTreeTests.SelectPathOfAPinnedFolderSelectsItUnderThisPC
                    - Projects
                        Archive
                        Docs
                      + Source   <- selected

                """);
    }

    // Moving on to another folder takes the selected look off the pinned entry.
    [Test]
    public async Task SelectingAnotherFolderUnselectsThePinnedEntry()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Source"));
        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);

        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Source
                - This PC
                  - FolderTreeTests.SelectingAnotherFolderUnselectsThePinnedEntry
                    - Projects
                        Archive
                        Docs   <- selected
                      + Source

                """);
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
            })
            .Snapshot(
                """
                {
                  selected: [
                    Projects/Source
                  ],
                  tree:
                - Pinned
                    Source   <- selected
                - This PC
                  - FolderTreeTests.PickingAPinnedFolderMovesToItUnderThisPC
                    - Projects
                        Archive
                        Docs
                      + Source   <- selected

                }
                """);
    }

    // Outside every drive (a network share, say) the pinned entry itself is selected.
    [Test]
    public async Task SelectPathFallsBackToThePinnedEntry()
    {
        using var fixture = Fixture.Create();
        using var other = Fixture.Create(member: "SelectPathFallsBackToThePinnedEntryOther");
        using var tree = Create(fixture, new() { ShowHidden = false }, other.Root);

        tree.SelectPath(other.Root);
        await Pump.Loaded(tree);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Projects   <- selected
                - This PC
                  + FolderTreeTests.SelectPathFallsBackToThePinnedEntry

                """);
    }

    [Test]
    public async Task SelectPathSettlesForTheParentOfAHiddenFolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Cache"));
        await Pump.Loaded(tree);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.SelectPathSettlesForTheParentOfAHiddenFolder
                    + Projects   <- selected
                        Archive
                        Docs
                      + Source

                """);
    }

    // The folder above one that is not there is read on the way, and selected instead. Watched
    // only for that, it is let go once another is selected, rather than watched, not expanded,
    // until the tree is made again.
    [Test]
    public async Task TheFolderSettledForIsLetGoOnceAnotherIsSelected()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        tree.SelectPath(fixture.At("Source", "Gone", "Deeper"));
        await Pump.Loaded(tree);
        var settledFor = (tree.SelectedNode?.Text, tree.IsWatched(fixture.At("Source")));
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Assert.That(settledFor).IsEqualTo(("Source", true));
        await Assert.That(tree.IsWatched(fixture.At("Source"))).IsFalse();
    }

    // A pin, an unpin or a pin moved makes Pinned again, and leaves the folders under This PC
    // as they were: what was expanded stays so, and is still watched, rather than the whole tree
    // being built again and collapsed.
    [Test]
    public async Task PinningLeavesTheFoldersUnderThisPCAsTheyWere()
    {
        using var fixture = Fixture.Create();
        var pinned = new List<string> { fixture.At("Docs") };
        var drive = Path.GetDirectoryName(fixture.Root)!;
        using var tree = new FolderTree(new() { ShowHidden = false }, new(pinned, [], ShowComputer: true, _ => "", [drive]));
        _ = tree.Handle;
        tree.Rebuild();
        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);
        var source = tree.SelectedNode!.Parent!;

        pinned.Add(fixture.At("Archive"));
        tree.RebuildPlaces();
        await Pump.Loaded(tree);

        await Assert.That(tree.Nodes[0].Nodes.Cast<TreeNode>().Select(_ => _.Text)).IsEquivalentTo(["Docs", "Archive"]);
        await Assert.That(source.TreeView).IsEqualTo(tree);
        await Assert.That(source.IsExpanded).IsTrue();
        await Assert.That(tree.IsWatched(fixture.At("Source"))).IsTrue();
    }

    // Built again, for hidden folders shown say, the tree expands again what was expanded under
    // This PC, as it reads it again.
    [Test]
    public async Task RebuildingExpandsAgainWhatWasExpanded()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);
        tree.SelectedNode = null;

        tree.Rebuild();
        await Pump.Until(() => Expanded(tree).Contains(fixture.At("Source")));

        await Assert.That(Expanded(tree)).Contains(fixture.At("Source"));
        await Assert.That(Expanded(tree)).Contains(fixture.Root);
    }

    // A drive plugged in or removed is added or taken away, and the drives that stayed are left
    // as they were, expanded and watched.
    [Test]
    public async Task ADriveComingOrGoingLeavesTheOthersAsTheyWere()
    {
        using var fixture = Fixture.Create();
        var drive = Path.GetDirectoryName(fixture.Root)!;
        var drives = new List<string> { drive };
        using var tree = new FolderTree(new() { ShowHidden = false }, new([], [], ShowComputer: true, _ => "", drives));
        _ = tree.Handle;
        tree.Rebuild();
        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);
        var computer = tree.Nodes.Cast<TreeNode>().Last();
        var kept = computer.Nodes[0];

        drives.Add(fixture.At("Docs"));
        tree.RefreshDrives();
        var added = computer.Nodes.Cast<TreeNode>().Select(_ => (string)_.Tag!).ToList();
        drives.RemoveAt(1);
        tree.RefreshDrives();

        await Assert.That(added).IsEquivalentTo([drive, fixture.At("Docs")]);
        await Assert.That(computer.Nodes.Count).IsEqualTo(1);
        await Assert.That(computer.Nodes[0]).IsEqualTo(kept);
        await Assert.That(kept.IsExpanded).IsTrue();
        await Assert.That(tree.IsWatched(drive)).IsTrue();
    }

    // The shell reads a drive to describe it, which for one just plugged in, a disk spinning up
    // say, can be slow: it is described off the UI thread, named by its letter with a drive's
    // icon until it has been. Described again, on F5 or as drives come and go, it shows as it
    // was last described meanwhile.
    [Test]
    public async Task ADriveIsNamedByItsLetterUntilItHasBeenDescribed()
    {
        using var fixture = Fixture.Create();
        var drive = Path.GetDirectoryName(fixture.Root)!;
        var describing = new TaskCompletionSource<(string DisplayName, string TypeName, int Icon)>();
        var describe = ShellIcons.DescribeOffUiThread;

        // Only this test's drive waits: those of tests running alongside are described as ever.
        ShellIcons.DescribeOffUiThread = _ =>
        {
            if (Locations.Same(_, drive))
            {
                return describing.Task;
            }

            return describe(_);
        };
        try
        {
            using var tree = new FolderTree(new() { ShowHidden = false }, new([], [], ShowComputer: true, _ => "", [drive]));
            _ = tree.Handle;
            var node = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];
            var meanwhile = (node.Text, node.ImageIndex);
            describing.SetResult(("Backup", "Local Disk", ShellIcons.FolderIcon));
            await Pump.Until(() => node.Text == "Backup");

            describing = new();
            ShellIcons.ForgetDrives();
            tree.RefreshDrives();
            var describedAgain = node.Text;
            describing.SetResult(("Archive", "Local Disk", ShellIcons.FolderIcon));
            await Pump.Until(() => node.Text == "Archive");

            await Assert.That(meanwhile).IsEqualTo((drive, ShellIcons.DriveIcon));
            await Assert.That(describedAgain).IsEqualTo("Backup");
        }
        finally
        {
            ShellIcons.DescribeOffUiThread = describe;
        }
    }

    // Selecting a folder whose drive is slow to answer, a mapped drive offline say, or a disk
    // that has to spin up, doesn't keep the window waiting while its watcher starts: that is made
    // off the UI thread, and given to the folder once it has been.
    [Test]
    public async Task SelectingAFolderDoesNotWaitForItsWatcher()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);
        var make = tree.MakeWatcher;
        tree.MakeWatcher = (path, changed) =>
        {
            Thread.Sleep(1500);
            return make(path, changed);
        };
        var docs = tree.SelectedNode!.Parent!.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Docs");

        var started = Stopwatch.GetTimestamp();
        tree.SelectedNode = docs;
        var took = Stopwatch.GetElapsedTime(started);
        var watchedAtOnce = tree.IsWatched(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Assert.That(took).IsLessThan(TimeSpan.FromMilliseconds(500));
        await Assert.That(watchedAtOnce).IsTrue();
        await Assert.That(tree.HasWatcher(fixture.At("Docs"))).IsTrue();
    }

    static List<string> Expanded(FolderTree tree)
    {
        var expanded = new List<string>();
        Collect(tree.Nodes);
        return expanded;

        void Collect(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.IsExpanded &&
                    node.Tag is string path)
                {
                    expanded.Add(path);
                }

                Collect(node.Nodes);
            }
        }
    }

    // Gone from a pinned folder to a share that is in no drive and not pinned, nothing is
    // selected, and the pinned entry loses its selected look, rather than still looking like
    // the open folder.
    [Test]
    public async Task GoingToAShareInNoDriveUnselectsThePinnedEntry()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Source"));

        tree.SelectByUser(tree.Nodes[0].Nodes[0]);
        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);
        var picked = Dump(tree);

        tree.SelectPath(@"\\nas\share\x");
        await Pump.Loaded(tree);

        await Verify(
            new
            {
                picked,
                thenAShare = Dump(tree)
            })
            .Snapshot(
                """
                {
                  picked:
                - Pinned
                    Source   <- selected
                - This PC
                  - FolderTreeTests.GoingToAShareInNoDriveUnselectsThePinnedEntry
                    - Projects
                        Archive
                        Docs
                      + Source   <- selected
                ,
                  thenAShare:
                - Pinned
                    Source
                - This PC
                  - FolderTreeTests.GoingToAShareInNoDriveUnselectsThePinnedEntry
                    - Projects
                        Archive
                        Docs
                      + Source

                }
                """);
    }

    // With Expand to open folder off, as Explorer's tree is by default, nothing is read or
    // expanded: a folder is selected only where the tree already shows it, and where it doesn't,
    // nothing is, rather than the folder before.
    [Test]
    public async Task WithoutExpandingToTheOpenFolderOnlyAFolderShowingIsSelected()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false, ExpandToOpenFolder = false });

        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);
        var notShowing = Dump(tree);

        await Expand(tree, await Projects(tree));
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);
        var showing = Dump(tree);

        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);

        await Verify(
            new
            {
                notShowing,
                showing,
                thenOneNotShowing = Dump(tree)
            })
            .Snapshot(
                """
                {
                  notShowing:
                - This PC
                  + FolderTreeTests.WithoutExpandingToTheOpenFolderOnlyAFolderShowingIsSelected
                ,
                  showing:
                - This PC
                  - FolderTreeTests.WithoutExpandingToTheOpenFolderOnlyAFolderShowingIsSelected
                    - Projects
                        Archive
                        Docs   <- selected
                      + Source
                ,
                  thenOneNotShowing:
                - This PC
                  - FolderTreeTests.WithoutExpandingToTheOpenFolderOnlyAFolderShowingIsSelected
                    - Projects
                        Archive
                        Docs
                      + Source

                }
                """);
    }

    // Picked where it is pinned, a folder stays selected there, rather than handing on to the
    // folder under This PC, which would expand down to it.
    [Test]
    public async Task WithoutExpandingToTheOpenFolderAPickedPinnedFolderStaysSelected()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false, ExpandToOpenFolder = false }, fixture.At("Source"));
        var selected = new List<string>();
        tree.PathSelected += selected.Add;

        tree.SelectByUser(tree.Nodes[0].Nodes[0]);
        // As the window does once the tab has gone there.
        tree.SelectPath(fixture.At("Source"));
        await Pump.For(TimeSpan.FromMilliseconds(100));
        var picked = Dump(tree);

        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        await Verify(
            new
            {
                selected = selected.Select(fixture.Relative),
                picked,
                thenOneNotShowing = Dump(tree)
            })
            .Snapshot(
                """
                {
                  selected: [
                    Projects/Source
                  ],
                  picked:
                - Pinned
                    Source   <- selected
                - This PC
                  + FolderTreeTests.WithoutExpandingToTheOpenFolderAPickedPinnedFolderStaysSelected
                ,
                  thenOneNotShowing:
                - Pinned
                    Source
                - This PC
                  + FolderTreeTests.WithoutExpandingToTheOpenFolderAPickedPinnedFolderStaysSelected

                }
                """);
    }

    [Test]
    public async Task PinnedAndRecentAreSeparateSections()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false }, [fixture.At("Docs")], [fixture.At("Source")]);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Docs
                - Recent
                    Source
                - This PC
                  + FolderTreeTests.PinnedAndRecentAreSeparateSections

                """);
    }

    // Pinned and recent folders with the same name, in one section or across the two, are told
    // apart by the folder each is in. One alone in its name keeps its name.
    [Test]
    public async Task FoldersWithTheSameNameAreToldApart()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Deep"));
        Directory.CreateDirectory(fixture.At("Archive", "Deep"));
        using var tree = CreateWithRecent(
            fixture,
            new() { ShowHidden = false },
            [fixture.At("Source", "Deep"), fixture.At("Docs", "Deep"), fixture.At("Docs")],
            [fixture.At("Archive", "Deep")]);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Source\Deep
                    Docs\Deep
                    Docs
                - Recent
                    Archive\Deep
                - This PC
                  + FolderTreeTests.FoldersWithTheSameNameAreToldApart

                """);
    }

    [Test]
    public async Task RecentHiddenWhenTurnedOff()
    {
        using var fixture = Fixture.Create();
        using var tree = CreateWithRecent(fixture, new() { ShowHidden = false, ShowRecent = false }, [fixture.At("Docs")], [fixture.At("Source")]);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - Pinned
                    Docs
                - This PC
                  + FolderTreeTests.RecentHiddenWhenTurnedOff

                """);
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

        await Verify(Dump(tree))
            .Snapshot(
                """
                + Pinned
                    Docs
                - Recent
                    Source
                - This PC
                  + FolderTreeTests.CollapsedSectionsStartCollapsed

                """);
    }

    // Asked for a folder it has not caught up with, one just made or renamed, the tree settles
    // on the folder it is in, and selects it once it shows up there.
    [Test]
    public async Task SelectionCatchesUpWithAFolderThatShowsUpLater()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        tree.SelectPath(fixture.At("Fresh"));
        await Pump.Loaded(tree);
        var settled = tree.SelectedNode?.Tag;
        Directory.CreateDirectory(fixture.At("Fresh"));
        await Pump.Until(() => fixture.At("Fresh").Equals(tree.SelectedNode?.Tag));

        await Assert.That(settled).IsEqualTo(fixture.Root);
    }

    [Test]
    public async Task PicksUpFoldersAddedAndRemovedOnDisk()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);
        var projects = await Projects(tree);
        await Expand(tree, projects.Nodes[2]);

        Directory.Delete(fixture.At("Docs"), true);
        Directory.CreateDirectory(fixture.At("Build"));
        await Pump.Until(() => Names(projects).Contains("Build") && !Names(projects).Contains("Docs"));

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.PicksUpFoldersAddedAndRemovedOnDisk
                    - Projects   <- selected
                        Archive
                        Build
                      - Source
                          Deep

                """);
    }

    [Test]
    public async Task ExpandingAgainPicksUpChangesMadeWhileCollapsed()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);
        await Expand(tree, projects);
        projects.Collapse();

        Directory.Delete(fixture.At("Archive"), true);
        await Expand(tree, projects);

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.ExpandingAgainPicksUpChangesMadeWhileCollapsed
                    - Projects
                        Docs
                      + Source

                """);
    }

    // A collapsed folder is not watched, so a folder made in it, by git clone say, is not seen
    // until the folder is read again. Going there reads it again, on the way, so the new one is
    // selected, and it is there when the folder is expanded.
    [Test]
    public async Task AFolderMadeInACollapsedOneIsSelectedWhenGoneTo()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);
        await Expand(tree, projects);
        var source = projects.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Source");
        await Expand(tree, source);
        source.Collapse();
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);

        Directory.CreateDirectory(fixture.At("Source", "New"));
        tree.SelectPath(fixture.At("Source", "New"));
        await Pump.Until(() => tree.SelectedNode?.Tag as string == fixture.At("Source", "New"));

        await Assert.That(Names(source)).IsEquivalentTo(["Deep", "New"], CollectionOrdering.Matching);
    }

    // The folder open stays watched when it is collapsed in the tree, being the one selected:
    // nothing selects it again to watch it, so, let go of, a folder made in it and gone to
    // would leave the tree where it was.
    [Test]
    public async Task AFolderMadeInTheSelectedOneOnceCollapsedIsSelectedWhenGoneTo()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Source"));
        await Pump.Loaded(tree);
        var source = tree.SelectedNode!;
        await Expand(tree, source);
        source.Collapse();

        Directory.CreateDirectory(fixture.At("Source", "New"));
        tree.SelectPath(fixture.At("Source", "New"));
        await Pump.Until(() => tree.SelectedNode?.Tag as string == fixture.At("Source", "New"));

        await Assert.That(Names(source)).IsEquivalentTo(["Deep", "New"], CollectionOrdering.Matching);
    }

    // The window selects the folder open again straight after a rebuild, which is no reason to
    // leave what else was expanded collapsed.
    [Test]
    public async Task RebuildingThenSelectingStillExpandsAgainWhatWasExpanded()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Inner"));
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Source", "Deep"));
        await Pump.Loaded(tree);
        tree.SelectPath(fixture.At("Docs", "Inner"));
        await Pump.Loaded(tree);

        tree.Rebuild();
        tree.SelectPath(fixture.At("Docs", "Inner"));
        await Pump.Until(() => Expanded(tree).Contains(fixture.At("Source")));

        await Assert.That(Expanded(tree)).Contains(fixture.At("Docs"));
        await Assert.That(tree.SelectedNode?.Tag as string).IsEqualTo(fixture.At("Docs", "Inner"));
    }

    // Nor is one with no subfolders when its parent was read: one made in it since shows once it
    // is gone to.
    [Test]
    public async Task AFolderMadeInOneWithNoneIsSelectedWhenGoneTo()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);
        await Expand(tree, projects);
        var docs = projects.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Docs");
        var expanderBefore = docs.Nodes.Count > 0;

        Directory.CreateDirectory(fixture.At("Docs", "Drafts"));
        tree.SelectPath(fixture.At("Docs", "Drafts"));
        await Pump.Until(() => tree.SelectedNode?.Tag as string == fixture.At("Docs", "Drafts"));

        await Assert.That(expanderBefore).IsFalse();
        await Assert.That(Names(docs)).IsEquivalentTo(["Drafts"]);
    }

    // A folder whose only subfolders are hidden has nothing to expand to while hidden folders
    // aren't shown.
    [Test]
    public async Task OnlyHiddenSubfoldersGiveAnExpanderWhenHiddenAreShown()
    {
        using var fixture = Fixture.Create();
        var secret = Directory.CreateDirectory(fixture.At("Docs", "Secret"));
        secret.Attributes |= FileAttributes.Hidden;

        await Assert.That(await DocsHasExpander(fixture, showHidden: false)).IsFalse();
        await Assert.That(await DocsHasExpander(fixture, showHidden: true)).IsTrue();
    }

    static async Task<bool> DocsHasExpander(Fixture fixture, bool showHidden)
    {
        using var tree = Create(fixture, new() { ShowHidden = showHidden });
        var projects = await Projects(tree);
        await Expand(tree, projects);
        var docs = projects.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Docs");
        return docs.Nodes.Count > 0;
    }

    // The selected folder is kept up to date though it is not expanded, so making a folder in
    // an empty one, as New folder in the list does, gives it an expander, and taking that away
    // takes the expander too.
    [Test]
    public async Task SelectedFolderGetsAnExpanderOnceItHasASubfolder()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.SelectPath(fixture.At("Docs"));
        await Pump.Loaded(tree);
        var docs = tree.SelectedNode!;
        var expanderAtFirst = docs.Nodes.Count > 0;

        Directory.CreateDirectory(fixture.At("Docs", "Drafts"));
        await Pump.Until(() => Names(docs).Contains("Drafts"));

        Directory.Delete(fixture.At("Docs", "Drafts"));
        await Pump.Until(() => docs.Nodes.Count == 0);

        await Assert.That(expanderAtFirst).IsFalse();
    }

    // Reading an expanded folder again brings the expanders of the folders in it in line with
    // what is in them now.
    [Test]
    public async Task ReadingAFolderAgainUpdatesTheExpandersInIt()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var projects = await Projects(tree);
        await Expand(tree, projects);

        Directory.CreateDirectory(fixture.At("Archive", "2025"));
        Directory.Delete(fixture.At("Source", "Deep"), true);
        // A change Projects' own watcher sees, which has it read again.
        Directory.CreateDirectory(fixture.At("Build"));
        await Pump.Until(() => Names(projects).Contains("Build"));

        await Verify(Dump(tree))
            .Snapshot(
                """
                - This PC
                  - FolderTreeTests.ReadingAFolderAgainUpdatesTheExpandersInIt
                    - Projects
                      + Archive
                        Build
                        Docs
                        Source

                """);
    }

    // Painted through a buffer, as Explorer's own tree is, so dragging the splitter, which has
    // every row drawn again as the tree is resized, doesn't flicker. Asked of the window itself
    // (TVM_GETEXTENDEDSTYLE), since it paints itself, and TVS_EX_DOUBLEBUFFER is what buffers it.
    [Test]
    public async Task PaintsThroughABuffer()
    {
        const int getExtendedStyle = 0x112D;
        const nint doubleBuffer = 0x0004;
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });

        var style = NativeMethods.SendMessage(tree.Handle, getExtendedStyle, IntPtr.Zero, IntPtr.Zero);

        await Assert.That(style & doubleBuffer).IsEqualTo(doubleBuffer);
    }

    // Delete, sent to the tree's window as pressing it would be, sends the selected folder to the
    // Recycle Bin, as it does in the list. A section heading and This PC are not folders, and
    // pressing it on them does nothing.
    [Test]
    public async Task DeleteDeletesTheSelectedFolder()
    {
        const int keyDown = 0x0100;
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Docs"));
        var deleted = new List<(string Path, bool Permanent)>();
        tree.DeleteFolder = (path, permanent) => deleted.Add((path, permanent));

        tree.SelectPath(fixture.At("Archive"));
        await Pump.Loaded(tree);
        NativeMethods.SendMessage(tree.Handle, keyDown, (int)Keys.Delete, IntPtr.Zero);
        tree.SelectedNode = tree.Nodes[0];
        NativeMethods.SendMessage(tree.Handle, keyDown, (int)Keys.Delete, IntPtr.Zero);
        tree.SelectedNode = tree.Nodes.Cast<TreeNode>().Last();
        NativeMethods.SendMessage(tree.Handle, keyDown, (int)Keys.Delete, IntPtr.Zero);

        await Assert.That(tree.Nodes[0].Text).IsEqualTo("Pinned");
        await Assert.That(deleted).IsEquivalentTo(new List<(string Path, bool Permanent)> { (fixture.At("Archive"), false) });
    }

    // Delete and Create shortcut chosen from Show more options are done by the tree, as from its
    // own menu, so Ctrl+Z hears of them, rather than by the shell alone. Not for a drive, nor
    // This PC.
    [Test]
    public async Task DeleteAndCreateShortcutFromTheClassicMenuAreTheTrees()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var deleted = new List<string>();
        tree.DeleteFolder = (path, _) => deleted.Add(path);
        tree.SelectPath(fixture.At("Archive"));
        await Pump.Loaded(tree);
        var archive = tree.SelectedNode!;
        var computer = tree.Nodes.Cast<TreeNode>().Last();
        var drive = computer.Nodes[0];

        var deletes = tree.HandleShellVerb("delete", fixture.At("Archive"), archive);
        var links = tree.HandleShellVerb("link", fixture.At("Archive"), archive);
        var drives = tree.HandleShellVerb("delete", @"C:\", drive) ||
                     tree.HandleShellVerb("link", @"C:\", drive);
        var thisPC = tree.HandleShellVerb("link", (string)computer.Tag!, computer);

        await Assert.That((deletes, links, drives, thisPC)).IsEqualTo((true, true, false, false));
        await Assert.That(deleted).IsEquivalentTo([fixture.At("Archive")]);
        await Assert.That(File.Exists(fixture.At("Archive - Shortcut.lnk"))).IsTrue();
    }

    // The keys of the right-click menu's actions run its entries for the selected folder, where
    // the menu would let them: a Pinned entry can't be renamed, the section headings aren't
    // folders, and This PC's menu has only Properties of them. Shift+Delete deletes outright
    // what Delete sends to the Recycle Bin, and Ctrl+Shift+N is New's Folder, asking for a
    // folder in the selected one. Ctrl+Shift+C copies it as a path. Ctrl+A is not one of them.
    // Paste hangs on what is on the clipboard, which tests leave alone.
    [Test]
    public async Task KeysRunTheRightClickMenusEntries()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Docs"));
        var deleted = new List<(string Path, bool Permanent)>();
        tree.DeleteFolder = (path, permanent) => deleted.Add((path, permanent));
        var asked = new List<(string Folder, ShellNew.Entry? Type)>();
        tree.NewRequested += (folder, type) => asked.Add((folder, type));
        Keys[] keys =
        [
            Keys.F2,
            Keys.Delete,
            Keys.Shift | Keys.Delete,
            Keys.Control | Keys.X,
            Keys.Control | Keys.C,
            Keys.Control | Keys.Shift | Keys.C,
            Keys.Alt | Keys.Enter,
            Keys.Control | Keys.Shift | Keys.N,
            Keys.Control | Keys.A
        ];

        string Name(Keys key)
        {
            if (tree.KeyCommand(key) is not { } command)
            {
                return "-";
            }

            return command.Id ?? command.Text;
        }

        string Describe() =>
            string.Join(", ", keys.Select(Name));

        tree.SelectPath(fixture.At("Archive"));
        await Pump.Loaded(tree);
        var onFolder = Describe();
        tree.KeyCommand(Keys.Delete)!.Action();
        tree.KeyCommand(Keys.Shift | Keys.Delete)!.Action();
        tree.KeyCommand(Keys.Control | Keys.Shift | Keys.N)!.Action();
        tree.SelectedNode = tree.Nodes[0].Nodes[0];
        var onPinned = Describe();
        tree.SelectedNode = tree.Nodes[0];
        var onHeading = Describe();
        tree.SelectedNode = tree.Nodes.Cast<TreeNode>().Last();
        var onThisPC = Describe();

        await Assert.That(onFolder).IsEqualTo("rename, delete, delete, cut, copy, copyAsPath, properties, Folder, -");
        await Assert.That(onPinned).IsEqualTo("-, delete, delete, cut, copy, copyAsPath, properties, Folder, -");
        await Assert.That(onHeading).IsEqualTo("-, -, -, -, -, -, -, -, -");
        await Assert.That(onThisPC).IsEqualTo("-, -, -, -, -, -, properties, -, -");
        await Assert.That(deleted).IsEquivalentTo(new List<(string Path, bool Permanent)> { (fixture.At("Archive"), false), (fixture.At("Archive"), true) });
        await Assert.That(asked).IsEquivalentTo(new List<(string Folder, ShellNew.Entry? Type)> { (fixture.At("Archive"), null) });
    }

    // This PC's right-click menu has Explorer's entries for it, less Delete and Rename. Its
    // first entry collapses This PC when it is open and expands it when it is not, and Open in
    // new tab and Open in new window ask for This PC.
    [Test]
    public async Task ThisPCsMenu()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        var thisPC = tree.Nodes.Cast<TreeNode>().Last();
        var tabs = new List<string>();
        var windows = new List<string>();
        tree.NewTabRequested += tabs.Add;
        tree.NewWindowRequested += windows.Add;

        string Describe() =>
            string.Join(", ", tree.ThisPCCommands(thisPC).Select(_ => _?.Text ?? "-"));

        var expanded = Describe();
        tree.ThisPCCommands(thisPC)[0]!.Action();
        var collapsed = Describe();
        var commands = tree.ThisPCCommands(thisPC);
        commands.Single(_ => _?.Id == "openInNewTab")!.Action();
        commands.Single(_ => _?.Text == "Open in new window")!.Action();

        await Assert.That(expanded).IsEqualTo(
            "Collapse, -, Manage, Map network drive..., Disconnect network drive..., Open in new tab, " +
            "Open in new window, -, Add a network location, -, Properties");
        await Assert.That(thisPC.IsExpanded).IsFalse();
        await Assert.That(collapsed).StartsWith("Expand, -, Manage, ");
        await Assert.That(tabs).IsEquivalentTo(new List<string> { Locations.ThisPC });
        await Assert.That(windows).IsEquivalentTo(new List<string> { Locations.ThisPC });
    }

    // A folder clicked with Ctrl held, or with the middle button, sent to the tree's window as
    // Windows sends the click, opens in a new tab, and the folder the tab shows stays selected.
    // A second Ctrl+click soon after, a double click, opens another, and the middle button let
    // go over another folder than it went down on is no click. Ctrl held on This PC's expander
    // only closes it, Ctrl+Shift is a plain click, and a plain click still goes there.
    [Test]
    public async Task CtrlAndMiddleClickOpenInANewTab()
    {
        const int controlHeld = 0x0008;
        const int shiftHeld = 0x0004;
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Docs"), fixture.At("Source"));
        // Room for every row, so each can be clicked.
        tree.Size = new(400, 600);
        tree.SelectPath(fixture.At("Archive"));
        await Pump.Loaded(tree);
        var archive = tree.SelectedNode;
        var tabs = new List<string>();
        var selected = new List<string>();
        tree.NewTabRequested += tabs.Add;
        tree.PathSelected += selected.Add;
        var docs = tree.Nodes[0].Nodes[0];
        var source = tree.Nodes[0].Nodes[1];
        var thisPC = tree.Nodes.Cast<TreeNode>().Last();

        Click(tree, Centre(docs), controlHeld);
        DoubleClick(tree, Centre(docs), controlHeld);
        Click(tree, Centre(thisPC), controlHeld);
        MiddleClick(tree, Centre(source), Centre(source));
        MiddleClick(tree, Centre(docs), Centre(source));
        var whileOpening = (selected.Count, tree.SelectedNode == archive);
        // Closing This PC moves the selection up to it, as it would however it was closed.
        Click(tree, Expander(tree, thisPC), controlHeld);
        var closed = (tabs.Count, thisPC.IsExpanded);
        Click(tree, Centre(source), controlHeld | shiftHeld);
        Click(tree, Centre(docs), 0);
        await Pump.Loaded(tree);

        await Assert.That(string.Join(" | ", tabs)).IsEqualTo(
            string.Join(" | ", fixture.At("Docs"), fixture.At("Docs"), Locations.ThisPC, fixture.At("Source")));
        await Assert.That(whileOpening).IsEqualTo((0, true));
        await Assert.That(closed).IsEqualTo((4, false));
        await Assert.That(string.Join(" | ", selected)).IsEqualTo(
            string.Join(" | ", fixture.At("Source"), fixture.At("Docs")));
    }

    static Point Centre(TreeNode node) =>
        new(node.Bounds.Left + node.Bounds.Width / 2, node.Bounds.Top + node.Bounds.Height / 2);

    // The point on a node's row that is its expander.
    static Point Expander(FolderTree tree, TreeNode node)
    {
        var y = node.Bounds.Top + node.Bounds.Height / 2;
        for (var x = 0; x < node.Bounds.Left; x++)
        {
            if (tree.HitTest(x, y).Location == TreeViewHitTestLocations.PlusMinus)
            {
                return new(x, y);
            }
        }

        throw new($"{node.Text} has no expander");
    }

    // A click as Windows sends one: the button up waiting in the queue, for the tree, which
    // doesn't come back from the press until the button is up, to find.
    static void Click(FolderTree tree, Point point, int keys)
    {
        const int leftButtonDown = 0x0201;
        const int leftButtonUp = 0x0202;
        const int leftButton = 0x0001;
        PostMessage(tree.Handle, leftButtonUp, keys, Packed(point));
        NativeMethods.SendMessage(tree.Handle, leftButtonDown, leftButton | keys, Packed(point));
        Application.DoEvents();
    }

    // The second click of a double click, which comes as WM_LBUTTONDBLCLK in place of a press.
    static void DoubleClick(FolderTree tree, Point point, int keys)
    {
        const int leftButtonDoubleClick = 0x0203;
        const int leftButtonUp = 0x0202;
        const int leftButton = 0x0001;
        NativeMethods.SendMessage(tree.Handle, leftButtonDoubleClick, leftButton | keys, Packed(point));
        NativeMethods.SendMessage(tree.Handle, leftButtonUp, keys, Packed(point));
        Application.DoEvents();
    }

    static void MiddleClick(FolderTree tree, Point down, Point up)
    {
        const int middleButtonDown = 0x0207;
        const int middleButtonUp = 0x0208;
        const int middleButton = 0x0010;
        NativeMethods.SendMessage(tree.Handle, middleButtonDown, middleButton, Packed(down));
        NativeMethods.SendMessage(tree.Handle, middleButtonUp, 0, Packed(up));
        Application.DoEvents();
    }

    static IntPtr Packed(Point point) =>
        (point.Y << 16) | (point.X & 0xFFFF);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    static List<string> Names(TreeNode node) =>
        node.Nodes.Cast<TreeNode>().Select(_ => _.Text).ToList();

    // Expands a folder, and waits for it to be read, which the tree does off the UI thread.
    static Task Expand(FolderTree tree, TreeNode node)
    {
        node.Expand();
        return Pump.Loaded(tree);
    }

    // The fixture's Projects folder, under the stand-in drive.
    static async Task<TreeNode> Projects(FolderTree tree)
    {
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];
        await Expand(tree, drive);
        return drive.Nodes.Cast<TreeNode>().Single(_ => _.Text == "Projects");
    }

    // Held over a closed folder for a moment, a drag opens it, as Explorer's pane does, so a
    // folder inside a closed one can be dropped on.
    [Test]
    public async Task HeldOverAClosedFolderADragOpensIt()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false });
        tree.Size = new(300, 400);
        var clock = new Clock(new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        tree.Time = clock;
        var drive = tree.Nodes.Cast<TreeNode>().Last().Nodes[0];
        var at = new Point(drive.Bounds.Left + 5, drive.Bounds.Top + drive.Bounds.Height / 2);
        var closed = drive.IsExpanded;

        tree.FollowDrag(at);
        clock.Now += TimeSpan.FromMilliseconds(500);
        tree.FollowDrag(at);
        var early = drive.IsExpanded;
        clock.Now += TimeSpan.FromMilliseconds(400);
        tree.FollowDrag(at);
        await Pump.Loaded(tree);

        await Assert.That((closed, early)).IsEqualTo((false, false));
        await Assert.That(drive.IsExpanded).IsTrue();
    }

    // Near the top or bottom, a drag scrolls a line at a time, as Explorer's pane does, so a
    // folder out of sight can be dropped on.
    [Test]
    public async Task NearTheTopOrBottomADragScrolls()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Docs"), fixture.At("Archive"), fixture.At("Source"), fixture.At("Cache"));
        tree.Size = new(300, tree.ItemHeight * 4);
        var clock = new Clock(new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        tree.Time = clock;
        var bottom = new Point(10, tree.ClientSize.Height - 2);
        var start = tree.TopNode;

        tree.FollowDrag(bottom);
        var once = tree.TopNode;
        clock.Now += TimeSpan.FromMilliseconds(50);
        tree.FollowDrag(bottom);
        var tooSoon = tree.TopNode;
        clock.Now += TimeSpan.FromMilliseconds(100);
        tree.FollowDrag(bottom);
        var twice = tree.TopNode;
        clock.Now += TimeSpan.FromMilliseconds(100);
        tree.FollowDrag(new(10, 2));

        await Assert.That(once)!.IsNotEqualTo(start);
        await Assert.That(tooSoon).IsEqualTo(once);
        await Assert.That(twice)!.IsNotEqualTo(once);
        await Assert.That(tree.TopNode).IsEqualTo(once);
    }

    // A folder drags out of the tree, to the list, Explorer or another app, as Explorer's pane
    // allows; a drive as a shortcut only. A pinned folder dragged with the left button goes up
    // or down the Pinned list, with the right, out. This PC and the headings don't drag. Each
    // drag carries a picture, as Explorer's do.
    [Test]
    public async Task FoldersDragOutOfTheTree()
    {
        using var fixture = Fixture.Create();
        using var tree = Create(fixture, new() { ShowHidden = false }, fixture.At("Docs"));
        var drags = new List<string>();
        tree.StartDrag = (data, allowed) =>
        {
            var what = "pinned";
            if (((IDataObject) data).GetData(DataFormats.FileDrop) is string[] paths)
            {
                what = string.Join(",", paths.Select(_ => Path.GetFileName(_.TrimEnd('\\'))));
            }

            drags.Add($"{what}: {allowed}, picture {DragImage.SizeOf(data)?.Width}");
        };
        var pinnedHeading = tree.Nodes[0];
        var pinned = pinnedHeading.Nodes[0];
        var computer = tree.Nodes.Cast<TreeNode>().Last();
        var drive = computer.Nodes[0];
        drive.Expand();
        await Pump.Loaded(tree);
        var folder = drive.Nodes.Cast<TreeNode>().Single(_ => Locations.Same(_.Tag as string, fixture.Root));

        tree.DragNode(pinned, MouseButtons.Left);
        tree.DragNode(pinned, MouseButtons.Right);
        tree.DragNode(folder, MouseButtons.Left);
        var beforeDrive = drags.Count;
        tree.DragNode(drive, MouseButtons.Left);
        tree.DragNode(computer, MouseButtons.Left);
        tree.DragNode(pinnedHeading, MouseButtons.Left);

        await Assert.That(drags.Take(beforeDrive)).IsEquivalentTo(
            [
                "pinned: Move, picture 96",
                "Docs: Copy, Move, Link, picture 96",
                $"{Path.GetFileName(fixture.Root)}: Copy, Move, Link, picture 96"
            ],
            CollectionOrdering.Matching);
        await Assert.That(drags.Skip(beforeDrive).Single()).EndsWith(": Link, picture 96");
    }

    // A tab on a share has its place in the tree, as in Explorer: under the machine it is on,
    // in Network, after This PC, the machine added there if it isn't yet, with what it shares
    // beneath it, those named to be hidden left out while hidden items are.
    [Test]
    [NotInParallel("shares")]
    public async Task AShareIsUnderItsMachineInNetwork()
    {
        using var fixture = Fixture.Create();
        var listShares = FolderTree.ListShares;
        FolderTree.ListShares = _ => [("Docs", false), ("Backup$", true), ("Archive", false)];
        try
        {
            using var tree = Create(fixture, new() { ShowHidden = false });
            tree.MakeWatcher = (_, _) => null;
            var before = tree.Nodes.Cast<TreeNode>().Select(_ => _.Text).ToList();

            tree.SelectPath(@"\\lodetest\Docs");
            await Pump.Loaded(tree);
            var network = tree.Nodes.Cast<TreeNode>().Last();
            var machine = network.Nodes.Cast<TreeNode>().Single();

            await Assert.That(before).DoesNotContain("Network");
            await Assert.That(network.Text).IsEqualTo("Network");
            await Assert.That(machine.Text).IsEqualTo("lodetest");
            await Assert.That(machine.Nodes.Cast<TreeNode>().Select(_ => _.Text)).IsEquivalentTo(["Archive", "Docs"], CollectionOrdering.Matching);
            await Assert.That(tree.SelectedNode?.Tag).IsEqualTo(@"\\lodetest\Docs");
        }
        finally
        {
            FolderTree.ListShares = listShares;
        }
    }

    // A folder pinned on a machine puts the machine in Network from the start. One whose shares
    // can't be listed, offline say, has the pinned share under it all the same, selected there
    // as the tab's folder.
    [Test]
    [NotInParallel("shares")]
    public async Task APinnedShareOnAMachineOffline()
    {
        using var fixture = Fixture.Create();
        var listShares = FolderTree.ListShares;
        FolderTree.ListShares = _ => throw new System.ComponentModel.Win32Exception(53);
        try
        {
            using var tree = Create(fixture, new() { ShowHidden = false }, @"\\lodetest\Projects");
            tree.MakeWatcher = (_, _) => null;
            var network = tree.Nodes.Cast<TreeNode>().Last();
            var machines = network.Nodes.Cast<TreeNode>().Select(_ => _.Text).ToList();

            tree.SelectPath(@"\\lodetest\Projects");
            await Pump.Loaded(tree);

            await Assert.That(network.Text).IsEqualTo("Network");
            await Assert.That(machines).IsEquivalentTo(["lodetest"]);
            await Assert.That(tree.SelectedNode?.Tag).IsEqualTo(@"\\lodetest\Projects");
            await Assert.That(tree.SelectedNode?.Parent?.Text).IsEqualTo("lodetest");
        }
        finally
        {
            FolderTree.ListShares = listShares;
        }
    }

    // A share its machine doesn't list is gone into all the same: one of its administrative
    // shares, c$ say, which a machine never lists. The share is put under the machine, among the
    // shares it does list, read already for one of those gone to first, and the tree goes on
    // down to where the tab is, rather than stopping on the machine.
    [Test]
    [NotInParallel("shares")]
    public async Task AShareItsMachineDoesNotListIsGoneInto()
    {
        using var fixture = Fixture.Create();
        var listShares = FolderTree.ListShares;
        var findSharedSubfolders = FolderTree.FindSharedSubfolders;
        FolderTree.ListShares = _ => [("Docs", false)];
        FolderTree.FindSharedSubfolders = FindOnLodetest;
        try
        {
            using var tree = Create(fixture, new() { ShowHidden = false });
            tree.MakeWatcher = (_, _) => null;
            tree.SelectPath(@"\\lodetest\Docs");
            await Pump.Loaded(tree);

            tree.SelectPath(@"\\lodetest\c$\Temp");
            await Pump.Loaded(tree);
            var machine = tree.Nodes.Cast<TreeNode>().Last().Nodes.Cast<TreeNode>().Single();

            await Assert.That(machine.Nodes.Cast<TreeNode>().Select(_ => _.Text)).IsEquivalentTo(["c$", "Docs"], CollectionOrdering.Matching);
            await Assert.That(tree.SelectedNode?.Tag).IsEqualTo(@"\\lodetest\c$\Temp");
        }
        finally
        {
            FolderTree.ListShares = listShares;
            FolderTree.FindSharedSubfolders = findSharedSubfolders;
        }
    }

    // A machine that won't say what it shares at all, as WSL's and some NASes won't, still has
    // the share gone to under it, and the tree goes on down to where the tab is. Collapsed and
    // expanded again, it is asked again what it shares, the share gone to staying meanwhile,
    // so once it says, the rest show too.
    [Test]
    [NotInParallel("shares")]
    public async Task AMachineThatWontSayWhatItSharesHasTheShareGoneTo()
    {
        using var fixture = Fixture.Create();
        var listShares = FolderTree.ListShares;
        var findSharedSubfolders = FolderTree.FindSharedSubfolders;
        var answers = false;
        FolderTree.ListShares = _ => answers ? [("Docs", false)] : throw new System.ComponentModel.Win32Exception(5);
        FolderTree.FindSharedSubfolders = FindOnLodetest;
        try
        {
            using var tree = Create(fixture, new() { ShowHidden = false });
            tree.MakeWatcher = (_, _) => null;

            tree.SelectPath(@"\\lodetest\c$\Temp");
            await Pump.Loaded(tree);
            var reached = tree.SelectedNode?.Tag;
            var machine = tree.Nodes.Cast<TreeNode>().Last().Nodes.Cast<TreeNode>().Single();
            List<string> Shares() =>
                machine.Nodes.Cast<TreeNode>().Select(_ => _.Text).ToList();
            var refusing = Shares();

            // Somewhere else, so the machine is no longer kept up to date for being on the way
            // to what is selected.
            tree.SelectPath(fixture.Root);
            await Pump.Loaded(tree);
            machine.Collapse();
            machine.Expand();
            await Pump.Loaded(tree);
            var expandedAgain = machine.IsExpanded;
            var stillRefusing = Shares();

            answers = true;
            machine.Collapse();
            machine.Expand();
            await Pump.Loaded(tree);

            await Assert.That(reached).IsEqualTo(@"\\lodetest\c$\Temp");
            await Assert.That(refusing).IsEquivalentTo(["c$"]);
            await Assert.That(expandedAgain).IsTrue();
            await Assert.That(stillRefusing).IsEquivalentTo(["c$"]);
            await Assert.That(Shares()).IsEquivalentTo(["c$", "Docs"], CollectionOrdering.Matching);
        }
        finally
        {
            FolderTree.ListShares = listShares;
            FolderTree.FindSharedSubfolders = findSharedSubfolders;
        }
    }

    // What \\lodetest's c$ has: Temp, with nothing in it.
    static IEnumerable<(string Name, string FullPath)> FindOnLodetest(string path, EnumerationOptions options, SystemItems? hiddenSystem)
    {
        if (Locations.Same(path, @"\\lodetest\c$"))
        {
            return [("Temp", @"\\lodetest\c$\Temp")];
        }

        return [];
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
        Append(builder, tree.Nodes, 0);
        return builder.ToString();
    }

    static void Append(StringBuilder builder, TreeNodeCollection nodes, int depth)
    {
        foreach (TreeNode node in nodes)
        {
            // The stand-in child of a folder that hasn't been expanded yet.
            if (node.Tag is not (string or FolderTree.Section))
            {
                continue;
            }

            var marker = node.Nodes.Count == 0 ? " " : node.IsExpanded ? "-" : "+";
            // Drawn as selected, which a pinned entry for the selected folder is too.
            var selected = node.IsSelected ? "   <- selected" : "";
            builder.Append(' ', depth * 2);
            builder.AppendLine($"{marker} {node.Text}{selected}");
            Append(builder, node.Nodes, depth + 1);
        }
    }
}
