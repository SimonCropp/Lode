/// <summary>
/// Tabs moving between windows, and into windows of their own.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
// Shows windows, so not alongside the captures in MainFormTests.
public class WindowsTests
{
    [Test]
    public async Task MoveTabToAnotherWindow()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root);
        var moved = source.Views.Last();

        source.MoveTab((TabPage)moved.Parent!, target);

        // Still tied to the window it came from, this would retitle that one instead.
        moved.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(moved);

        await Verify(
            new
            {
                source = Describe(source),
                target = Describe(target)
            });
    }

    // Dropped between two of another window's tabs, a tab goes in there, and is the one shown.
    [Test]
    public async Task MoveTabBetweenAnotherWindowsTabs()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root, fixture.At("Archive"));

        source.MoveTab((TabPage)source.Views.Last().Parent!, target, 1);

        await Assert.That(Titles(target)).IsEquivalentTo(["Projects", "Docs", "Archive"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(target.ActiveView!.CurrentPath).IsEqualTo(fixture.At("Docs"));
    }

    // A tab dragged along the row goes where it is dragged, and stays the one shown: the window
    // doesn't show another on the way, nor bring itself in line again. Saved in that order.
    [Test]
    public async Task DraggingATabAlongTheRow()
    {
        const int leftButtonDown = 0x0201;
        const int leftButtonUp = 0x0202;
        const int mouseMove = 0x0200;
        using var fixture = Fixture.Create();
        var settings = new Settings();
        using var form = await Show(fixture, settings, fixture.Root, fixture.At("Docs"), fixture.At("Source"));
        var tabs = (ClosableTabControl)form.ActiveView!.Parent!.Parent!;
        var source = tabs.GetTabRect(2);
        var middle = source.Top + source.Height / 2;
        void Send(int message, int left) =>
            NativeMethods.SendMessage(tabs.Handle, message, 1, (middle << 16) | (left & 0xFFFF));

        // Pressed, the tab is selected, and shown, before any drag.
        Send(leftButtonDown, source.Left + 10);
        var showing = form.ActiveView!;
        var synced = new List<string>();
        form.Synced += _ => synced.Add(Path.GetFileName(_.CurrentPath));
        Send(mouseMove, tabs.GetTabRect(0).Left + 10);
        Send(leftButtonUp, tabs.GetTabRect(0).Left + 10);
        var titles = Titles(form);
        var shown = form.ActiveView;
        form.Close();

        await Assert.That(titles).IsEquivalentTo(["Source", "Projects", "Docs"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(showing.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(shown).IsSameReferenceAs(showing);
        await Assert.That(synced).IsEmpty();
        await Assert.That(settings.Tabs.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["Source", "Projects", "Docs"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // A column sized in one window is that wide in every tab of every window, the tab in the
    // background included.
    [Test]
    public async Task ColumnWidthsGoToEveryWindow()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        using var first = await Show(fixture, settings, fixture.Root, fixture.At("Docs"));
        using var second = await Show(fixture, settings, fixture.At("Source"));
        var list = first.ActiveView!.Controls.OfType<ListView>().Single();

        Header.LetGoOfDivider(list, 0, first.ActiveView.LogicalToDeviceUnits(250));

        var widths = first.Views
            .Concat(second.Views)
            .Where(_ => _ != first.ActiveView)
            .Select(_ => _.ColumnWidth(0) == _.LogicalToDeviceUnits(250))
            .ToList();
        await Assert.That(widths).IsEquivalentTo([true, true]);
    }

    static List<string> Titles(MainForm form) =>
        form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList();

    [Test]
    public async Task MovingTheLastTabClosesItsWindow()
    {
        using var fixture = Fixture.Create();
        var source = await Show(fixture, fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root);

        source.MoveTab((TabPage)source.Views.Single().Parent!, target);
        Application.DoEvents();

        await Verify(
            new
            {
                sourceClosed = source.IsDisposed,
                target = Describe(target)
            });
    }

    // While Lode runs, the tabs saved are those of the window used last, as they are when it
    // exits, so a Lode ended without closing comes back with them. A window with no folder left
    // in it, its last tab moved to another window, saves none in their place as it closes.
    [Test]
    public async Task TheWindowUsedLastSavesItsTabsWhileItIsOpen()
    {
        const int activate = 0x0006;
        using var fixture = Fixture.Create();
        var settings = new Settings();
        var first = await Show(fixture, settings, fixture.At("Docs"));
        using var second = await Show(fixture, settings, fixture.Root, fixture.At("Source"));
        NativeMethods.SendMessage(second.Handle, activate, 1, IntPtr.Zero);

        first.SaveTabs();
        var fromTheOther = settings.Tabs.ToList();
        second.SaveTabs();
        var fromTheOneUsedLast = settings.Tabs.Select(_ => Path.GetFileName(_)).ToList();
        first.MoveTab((TabPage)first.Views.Single().Parent!, second);
        Application.DoEvents();

        await Assert.That(fromTheOther).IsEmpty();
        await Assert.That(fromTheOneUsedLast).IsEquivalentTo(["Projects", "Source"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(first.IsDisposed).IsTrue();
        await Assert.That(settings.Tabs.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["Projects", "Source"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task MoveTabToNewWindow()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        var opened = new List<MainForm>();
        MainForm.WindowOpened += opened.Add;
        try
        {
            using var window = source.MoveTabToNewWindow((TabPage)source.Views.Last().Parent!, new(-20000, -20000));
            Application.DoEvents();

            await Verify(
                new
                {
                    source = Describe(source),
                    window = Describe(window),
                    announced = opened.SequenceEqual([window])
                });
        }
        finally
        {
            MainForm.WindowOpened -= opened.Add;
        }
    }

    // Shown already, with its title and address bar for the folder: the tab control has no
    // handle until then, so selecting the tab says nothing of it.
    [Test]
    public async Task NewWindowShowsItsFolder()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.Root, fixture.At("Docs"));
        var opened = new List<MainForm>();
        MainForm.WindowOpened += opened.Add;
        try
        {
            source.NewWindow(fixture.At("Docs"));
            using var window = opened.Single();
            var address = (Breadcrumb)window.Controls.Find("address", true)[0].Parent!;

            await Assert.That(window.ActiveView!.CurrentPath).IsEqualTo(fixture.At("Docs"));
            await Assert.That(window.Text).IsEqualTo(source.Text);
            await Assert.That(address.Path).IsEqualTo(fixture.At("Docs"));
        }
        finally
        {
            MainForm.WindowOpened -= opened.Add;
        }
    }

    // Closing the last window showing hides it to the tray. One already there, a window closed
    // before another opened for a folder from another app, closes for real, so the tray holds
    // one window at most: the one closed last. A window closed while another shows closes.
    [Test]
    public async Task OneWindowInTheTrayAtMost()
    {
        using var fixture = Fixture.Create();
        MainForm.HideLastWindowOnClose = true;
        try
        {
            using var first = await Show(fixture, fixture.Root);
            first.Close();
            var firstInTray = (first.IsDisposed, first.Visible);

            using var second = await Show(fixture, fixture.At("Docs"));
            using var third = await Show(fixture, fixture.At("Source"));
            third.Close();
            var thirdClosedWhileSecondShows = third.IsDisposed;
            second.Close();
            Application.DoEvents();

            await Assert.That(firstInTray).IsEqualTo((false, false));
            await Assert.That(thirdClosedWhileSecondShows).IsTrue();
            await Assert.That(first.IsDisposed).IsTrue();
            await Assert.That(second.IsDisposed).IsFalse();
            await Assert.That(second.Visible).IsFalse();
        }
        finally
        {
            MainForm.HideLastWindowOnClose = false;
        }
    }

    // Maximized and then minimized, a window comes back maximized, and so is saved as maximized
    // for the next start, as one closed from the tray or by Windows shutting down is.
    [Test]
    public async Task MinimizedKeepsWhetherItWasMaximized()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);

        form.WindowState = FormWindowState.Maximized;
        form.WindowState = FormWindowState.Minimized;
        var fromMaximized = form.WasMaximized;
        form.WindowState = FormWindowState.Normal;
        form.WindowState = FormWindowState.Minimized;
        var fromNormal = form.WasMaximized;

        await Assert.That(fromMaximized).IsTrue();
        await Assert.That(fromNormal).IsFalse();
    }

    [Test]
    public async Task CloseTabsToTheLeftAndRight()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"), fixture.At("Source"), fixture.At("Archive"), fixture.At("Source", "Deep"));
        var middle = (TabPage)form.Views.ElementAt(2).Parent!;

        form.CloseTabsToTheRight(middle);
        var afterRight = Describe(form);
        form.CloseTabsToTheLeft(middle);

        await Verify(
            new
            {
                afterRight,
                afterLeft = Describe(form)
            });
    }

    // Two folders with the same name are told apart by the folder each is in when they are tabs
    // in one window. In two windows they are not: each window is seen on its own. Each goes back
    // to its name alone once the other is no longer beside it.
    [Test]
    public async Task FoldersWithTheSameNameAreToldApart()
    {
        using var fixture = Fixture.Create();
        Directory.CreateDirectory(fixture.At("Docs", "Deep"));
        using var first = await Show(fixture, fixture.At("Source", "Deep"), fixture.At("Docs", "Deep"));
        var twoTabs = Describe(first);

        using var second = await Show(fixture, fixture.Root);
        var moved = (TabPage)first.Views.Last().Parent!;
        first.MoveTab(moved, second);
        var twoWindows = new
        {
            first = Describe(first),
            second = Describe(second)
        };

        second.MoveTab(moved, first);
        var movedBack = Describe(first);

        first.ActiveView!.Navigate(fixture.At("Docs"));
        await Pump.Loaded(first.ActiveView);
        var navigatedAway = Describe(first);

        first.CloseTabsToTheRight((TabPage)first.Views.First().Parent!);

        await Verify(
            new
            {
                twoTabs,
                twoWindows,
                movedBack,
                navigatedAway,
                oneTab = Describe(first)
            });
    }

    // Most recently closed first, each at the folder it was showing.
    [Test]
    public async Task ReopenClosedTabs()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"), fixture.At("Source"));
        form.CloseOtherTabs((TabPage)form.Views.First().Parent!);
        var afterClose = Describe(form);

        form.ReopenClosedTab();
        await Pump.Loaded(form.ActiveView!);
        var reopenedOnce = Describe(form);
        form.ReopenClosedTab();
        await Pump.Loaded(form.ActiveView!);

        await Verify(
            new
            {
                afterClose,
                reopenedOnce,
                reopenedTwice = Describe(form),
                nothingLeft = !form.CanReopenClosedTab
            });
    }

    // One whose folder has gone since, with the folder it was in, opens at the nearest folder
    // still there, as a tab does when its folder is deleted, rather than as an empty This PC.
    [Test]
    public async Task ReopeningATabWhoseFolderHasGone()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Source", "Deep"));
        form.CloseOtherTabs((TabPage)form.Views.First().Parent!);
        Directory.Delete(fixture.At("Source"), true);

        form.ReopenClosedTab();
        await Pump.Loaded(form.ActiveView!);

        await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(fixture.Root);
        await Assert.That(form.ActiveView.Entries.Select(_ => _.Name)).Contains("Docs");
        await Assert.That(form.CanReopenClosedTab).IsFalse();
    }

    // With closing unused tabs on, a tab not looked at for the hours given closes, the one looked
    // at longest ago first, so Reopen closed tab brings back the other first. The tab the window
    // shows stays however long it has shown, and one opened in the background has the hours from
    // when it opened. Off, as it is unless chosen, nothing closes.
    [Test]
    public async Task TabsNotLookedAtCloseAfterTheHoursGiven()
    {
        using var fixture = Fixture.Create();
        var clock = new Clock(new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        MainForm.Time = clock;
        try
        {
            var settings = new Settings
            {
                UnusedTabHours = 8
            };
            using var form = await Show(fixture, settings, fixture.Root, fixture.At("Docs"), fixture.At("Source"));
            var showing = form.ActiveView!;

            // 10:00, Docs looked at, then back to Source.
            clock.Now = clock.Now.AddHours(1);
            var docs = form.Views.ElementAt(1);
            Pick(docs);
            await Pump.Loaded(docs);
            Pick(showing);

            // 11:00, Archive opened in the background.
            clock.Now = clock.Now.AddHours(1);
            form.AddTab(fixture.At("Archive"), false);

            // 18:30: Projects 9.5 hours unused, Docs 8.5, Archive 7.5.
            clock.Now = clock.Now.AddHours(7.5);
            form.CloseUnusedTabs();
            var off = Describe(form);

            settings.CloseUnusedTabs = true;
            form.CloseUnusedTabs();
            var on = Describe(form);

            form.ReopenClosedTab();
            await Pump.Loaded(form.ActiveView!);
            var reopened = Describe(form);

            // 19:00: Archive 8 hours unused, untouched by the tabs closed and reopened beside it.
            clock.Now = clock.Now.AddHours(0.5);
            form.CloseUnusedTabs();

            await Verify(
                new
                {
                    off,
                    on,
                    reopened,
                    archiveToo = Describe(form)
                });
        }
        finally
        {
            MainForm.Time = TimeProvider.System;
        }
    }

    // When each tab was last looked at is saved with the tabs, so after a restart, an update's
    // say, each is as unused as it was rather than starting the hours again. The tab that was
    // active stays, however long ago it was looked at, and is saved as looked at until the
    // window closed.
    [Test]
    public async Task RestoredTabsAreAsUnusedAsTheyWere()
    {
        using var fixture = Fixture.Create();
        var clock = new Clock(new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        MainForm.Time = clock;
        try
        {
            var settings = new Settings
            {
                CloseUnusedTabs = true,
                UnusedTabHours = 8,
                Tabs = [fixture.Root, fixture.At("Docs"), fixture.At("Source")],
                TabsLastShown = [clock.Now.AddHours(-9), clock.Now.AddHours(-4), clock.Now.AddHours(-9)],
                ActiveTab = 2
            };
            using var form = await Show(fixture, settings);
            form.CloseUnusedTabs();
            var restored = Describe(form);

            clock.Now = clock.Now.AddHours(1);
            form.Close();

            await Verify(
                new
                {
                    restored,
                    saved = settings.Tabs
                        .Select(Path.GetFileName)
                        .Zip(settings.TabsLastShown, (tab, shown) => $"{tab} {shown:HH:mm}")
                });
        }
        finally
        {
            MainForm.Time = TimeProvider.System;
        }
    }

    // Another launch's folders, a GitHub Desktop "Show in Explorer" say: one a tab in any
    // window already shows, however its path is written, selects that tab, and the rest open as
    // tabs in the window used last, each once.
    [Test]
    public async Task LaunchedFoldersOpenAsTabs()
    {
        using var fixture = Fixture.Create();
        using var older = await Show(fixture, fixture.Root, fixture.At("Docs"), fixture.At("Source"));
        using var recent = await Show(fixture, fixture.At("Archive"));
        var paths = MainForm.Openable(
        [
            fixture.At("docs"),
            fixture.At("Source", "Deep"),
            fixture.At("Source", "Deep") + @"\"
        ]);

        var front = WindowContext.OpenInTabs([recent, older], paths);
        await Pump.Loaded(older.ActiveView!);
        await Pump.Loaded(recent.ActiveView!);

        await Verify(
            new
            {
                front = front == recent ? "recent" : "older",
                recent = Describe(recent),
                older = Describe(older)
            });
    }

    // A file opens in the tab already showing its folder, with the file selected, without a
    // second step back to the same folder.
    [Test]
    public async Task ALaunchedFileIsSelectedInTheTabShowingItsFolder()
    {
        using var fixture = Fixture.Create();
        using var older = await Show(fixture, fixture.At("Source"), fixture.Root);
        using var recent = await Show(fixture, fixture.At("Archive"));

        var front = WindowContext.OpenInTabs([recent, older], [fixture.At("Source", "main.aleph")]);
        await Pump.Loaded(older.ActiveView!);

        await Verify(
            new
            {
                front = front == recent ? "recent" : "older",
                recent = Describe(recent),
                older = Describe(older),
                selected = older.ActiveView!.SelectedEntries().Select(_ => _.Name).ToList(),
                canGoBack = older.ActiveView.CanGoBack
            });
    }

    // Only the tab showing reads its folder. The rest, the tabs a window opens with and those
    // opened in the background alike, have their names but read nothing until they are picked.
    [Test]
    public async Task BackgroundTabsReadTheirFoldersWhenPicked()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.At("Source"), fixture.Root);
        var restored = form.Views.First();
        var opened = form.AddTab(fixture.At("Docs"), false);
        var titles = form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList();
        var readInBackground = restored.Entries.Count + opened.Entries.Count;

        Pick(restored);
        await Pump.Until(() => restored.Entries.Count > 0);
        Pick(opened);
        await Pump.Until(() => opened.Entries.Count > 0);

        await Assert.That(titles).IsEquivalentTo(["Source", "Projects", "Docs"]);
        await Assert.That(readInBackground).IsEqualTo(0);
        await Assert.That(restored.Entries.Select(_ => _.Name)).Contains("main.aleph");
        await Assert.That(opened.Entries.Select(_ => _.Name)).Contains("guide.aleph");
    }

    static void Pick(FolderView view)
    {
        var page = (TabPage)view.Parent!;
        ((TabControl)page.Parent!).SelectedTab = page;
    }

    // In the tray, the tabs nobody was looking at let go of their folders. The one that was
    // showing keeps its rows. A tab let go is read again when it is picked, with what was
    // selected in it selected again.
    [Test]
    public async Task TabsNobodyWasLookingAtAreLetGoInTheTray()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.At("Source"), fixture.Root);
        var showing = form.ActiveView!;
        var other = form.Views.First();
        Pick(other);
        other.Reveal(fixture.At("Source", "main.aleph"));
        await Pump.Loaded(other);
        Pick(showing);
        await Pump.Loaded(showing);

        form.Hide();
        form.ReleaseMemory();
        var otherHeld = other.Entries.Count;
        var showingHeld = showing.Entries.Count;

        form.Show();
        var showingBack = showing.Entries.Count;
        Pick(other);
        await Pump.Until(() => other.Entries.Count > 0);

        await Assert.That(otherHeld).IsEqualTo(0);
        await Assert.That(showingHeld).IsGreaterThan(0);
        await Assert.That(showingBack).IsEqualTo(showingHeld);
        await Assert.That(other.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(other.SelectedEntries().Select(_ => _.Name)).IsEquivalentTo(["main.aleph"]);
    }

    // Hidden, as closing the last window to the tray hides it, a window leaves changes on disk
    // unread until it is shown again.
    [Test]
    public async Task LeavesChangesUnreadWhileHidden()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        var view = form.ActiveView!;

        form.Hide();
        await File.WriteAllBytesAsync(fixture.At("arrived.aleph"), new byte[3_000]);

        // Several times as long as a change takes to be read by a window that is showing.
        await Pump.For(TimeSpan.FromSeconds(2));
        var readWhileHidden = view.Entries.Any(_ => _.Name == "arrived.aleph");

        form.Show();
        await Pump.Until(() => view.Entries.Any(_ => _.Name == "arrived.aleph"));

        await Assert.That(readWhileHidden).IsFalse();
    }

    // Which of the actions bar's buttons work with nothing selected, then with a file.
    [Test]
    public async Task ActionsBar()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        var nothingSelected = form.DescribeActions().ToList();
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);
        form.UpdateActions();

        await Verify(
            new
            {
                nothingSelected,
                fileSelected = form.DescribeActions().ToList()
            });
    }

    // On a display above 100% the form scales its controls after building them. The boxes in
    // the address bar and filter frames must still sit centred inside them, not pushed down and
    // over the right border.
    [Test]
    public async Task BoxesStayInsideTheirFramesWhenScaled()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.Scale(new SizeF(1.25f, 1.25f));
        var address = (TextBox)form.Controls.Find("address", true)[0];
        ((Breadcrumb)address.Parent!).BeginEdit();
        Application.DoEvents();

        await Verify(
            new
            {
                filter = Placement((TextBox)form.Controls.Find("filter", true)[0]),
                address = Placement(address)
            });
    }

    static object Placement(TextBox box)
    {
        var frame = box.Parent!.ClientSize;
        return new
        {
            insideFrame = box.Left >= 0 && box.Right <= frame.Width,
            gapAbove = box.Top,
            gapBelow = frame.Height - box.Bottom
        };
    }

    // The bar shows the buttons Options asks for, in the catalog's order, and each does what its
    // right-click menu entry would.
    [Test]
    public async Task ActionsBarFollowsOptions()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        settings.SetPlacement("openInNewTab", button: true, menu: true);
        settings.SetPlacement("cut", button: false, menu: true);
        settings.SetPlacement(LodeActions.More, button: true, menu: true);
        using var form = await Show(fixture, settings, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("Docs"));
        await Pump.Loaded(form.ActiveView);
        form.UpdateActions();
        var buttons = form.DescribeActions().ToList();

        form.RunAction("openInNewTab");
        await Pump.Loaded(form.Views.Last());

        await Verify(
            new
            {
                buttons,
                tabs = form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList()
            });
    }

    // Buttons nested under a parent share its drop-down, which stands where the first of them
    // would have, and lists them as See more would: here Copy as path, not a button by default,
    // joins Cut and Copy under Clipboard.
    [Test]
    public async Task NestedButtonsShareTheirParentsDropDown()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        foreach (var id in new[] { "cut", "copy", "copyAsPath" })
        {
            settings.SetPlacement(id, button: true, menu: true, buttonNested: true);
        }

        using var form = await Show(fixture, settings, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("Docs"));
        await Pump.Loaded(form.ActiveView);
        form.UpdateActions();

        await Verify(
            new
            {
                buttons = form.DescribeActions().ToList(),
                clipboard = form.ParentEntries("Clipboard").Select(DescribeCommand).ToList()
            });
    }

    // The git actions, nested on the bar, share one Git drop-down, which calls them what the
    // Git submenu does: Pull rather than Git pull.
    [Test]
    public async Task NestedGitButtonsAreNamedAsInTheGitSubmenu()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings
        {
            CodeDirectory = fixture.Root
        };
        foreach (var id in LodeActions.GitIds)
        {
            settings.SetPlacement(id, button: true, menu: true, buttonNested: true);
        }

        using var form = await Show(fixture, settings, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("Docs"));
        await Pump.Loaded(form.ActiveView);
        form.UpdateActions();

        await Verify(
            new
            {
                buttons = form.DescribeActions().ToList(),
                git = form.ParentEntries("Git").Select(DescribeCommand).ToList()
            });
    }

    // See more, at the end of the bar, lists the actions that are not buttons, greyed where
    // they do not apply: here, to a file.
    [Test]
    public async Task SeeMoreListsTheOtherActions()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);

        await Verify(form.OverflowCommands(Point.Empty).Select(DescribeCommand));
    }

    // Without the Claude desktop app or a coding agent, neither See more nor the palette offers
    // Open in Claude or Coding agent, even greyed out.
    [Test]
    public async Task NothingForAppsThatAreNotInstalled()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("Docs"));
        await Pump.Loaded(form.ActiveView);
        var claude = ClaudeDesktop.IsInstalled;
        var agents = CodingAgents.Installed;
        ClaudeDesktop.IsInstalled = () => false;
        CodingAgents.Installed = () => [];
        try
        {
            List<string> offered =
            [
                .. form.OverflowCommands(Point.Empty).Select(DescribeCommand),
                .. form.PaletteCommands().Select(_ => _.Name)
            ];

            await Assert.That(offered.Where(_ => _.Contains("Claude") || _.Contains("Coding agent"))).IsEmpty();
        }
        finally
        {
            ClaudeDesktop.IsInstalled = claude;
            CodingAgents.Installed = agents;
        }
    }

    // From the palette, Terminal, Terminal (admin), Open in Claude and Coding agent open in the
    // folder selected, as the right-click menu and the actions bar do, and in the tab's folder
    // with a file selected, which has none of its own.
    [Test]
    public async Task PaletteOpensInTheFolderSelected()
    {
        using var fixture = Fixture.Create();
        await File.WriteAllTextAsync(fixture.At("notes.txt"), "notes");
        using var form = await Show(fixture, fixture.Root);
        var claude = ClaudeDesktop.IsInstalled;
        var agents = CodingAgents.Installed;
        var startTerminal = FileOperations.StartTerminal;
        var startLink = ClaudeDesktop.StartLink;
        var opened = new List<string>();
        ClaudeDesktop.IsInstalled = () => true;
        CodingAgents.Installed = () => [new("Agent", @"C:\Tools\agent.exe")];
        FileOperations.StartTerminal = (tabs, admin) =>
            opened.AddRange(tabs.Select(_ => $"{(admin ? "admin " : "")}terminal {fixture.Relative(_.Directory)} {string.Join(" ", _.Command)}".TrimEnd()));
        ClaudeDesktop.StartLink = opened.Add;
        try
        {
            async Task<List<string>> Run(string selected)
            {
                opened.Clear();
                form.ActiveView!.Reveal(fixture.At(selected));
                await Pump.Loaded(form.ActiveView);
                var commands = form.PaletteCommands();
                foreach (var name in new[] { "Terminal", "Terminal (admin)", "Open in Claude", "Coding agent: Agent" })
                {
                    commands.Single(_ => _.Name == name).Action();
                }

                return opened
                    .Select(_ => _.Replace(Uri.EscapeDataString(fixture.Root), "{Root}"))
                    .ToList();
            }

            var folder = await Run("Docs");
            var file = await Run("notes.txt");

            await Verify(
                new
                {
                    folder,
                    file
                });
        }
        finally
        {
            ClaudeDesktop.IsInstalled = claude;
            CodingAgents.Installed = agents;
            FileOperations.StartTerminal = startTerminal;
            ClaudeDesktop.StartLink = startLink;
        }
    }

    // The ⋯ menu at the right of the address bar: Lode's own commands, none of the actions.
    [Test]
    public async Task WindowMenu()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);

        await Verify(form.WindowCommands().Select(DescribeCommand));
    }

    static string DescribeCommand(QuickMenu.Command? command)
    {
        if (command == null)
        {
            return "----";
        }

        if (command.Enabled)
        {
            return command.Text;
        }

        return $"{command.Text} (disabled)";
    }

    // Right-clicking a tab: Unpin for the fixture's folder, which is pinned, and Pin for Docs,
    // which is not, whichever tab is selected.
    [Test]
    public async Task TabMenu()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"));
        var pages = form.Views.Select(_ => (TabPage)_.Parent!).ToList();

        await Verify(
            new
            {
                pinned = DescribeMenu(form.TabMenu(pages[0])),
                notPinned = DescribeMenu(form.TabMenu(pages[1]))
            });
    }

    static List<string> DescribeMenu(ToolStripDropDown menu)
    {
        using (menu)
        {
            return menu.Items.Cast<ToolStripItem>().Select(DescribeItem).ToList();
        }
    }

    static string DescribeItem(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        if (item.Enabled)
        {
            return item.Text!;
        }

        return $"{item.Text} (disabled)";
    }

    // What Ctrl+Shift+P lists with nothing selected.
    [Test]
    public async Task PaletteCommands()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"));

        // New's file types depend on what is installed.
        var commands = form.PaletteCommands()
            .Where(_ => !_.Name.StartsWith("New: ") || _.Name == "New: Folder")
            .Select(_ => _.Shortcut.Length == 0 ? _.Name : $"{_.Name} ({_.Shortcut})");
        await Verify(commands);
    }

    // The tabs from last time, less one whose folder has gone since. The tab that was active
    // still is, a place earlier than it was.
    [Test]
    public async Task RestoresTheActiveTabWhenAnEarlierOneHasGone()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings
        {
            Tabs = [fixture.At("Gone"), fixture.At("Source"), fixture.At("Docs")],
            ActiveTab = 1
        };
        using var form = await Show(fixture, settings);

        await Assert.That(form.Views.Select(_ => _.CurrentPath)).IsEquivalentTo(
            [fixture.At("Source"), fixture.At("Docs")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(fixture.At("Source"));
    }

    // A tab that was inside a zip opens inside it again. A zip launched with is a file, shown
    // selected in its folder.
    [Test]
    public async Task TabsInsideAZipAreRestored()
    {
        using var fixture = Fixture.Create();
        var zip = fixture.At("Bundle.zip");
        await using (var archive = await ZipFile.OpenAsync(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Inner/deep.aleph");
        }

        var settings = new Settings
        {
            Tabs = [zip, Path.Combine(zip, "Inner")]
        };
        using var restored = await Show(fixture, settings);
        using var launched = await Show(fixture, new Settings(), zip);

        await Assert.That(restored.Views.Select(_ => _.CurrentPath)).IsEquivalentTo(
            [zip, Path.Combine(zip, "Inner")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(launched.ActiveView!.CurrentPath).IsEqualTo(fixture.Root);
    }

    // Closing the selected tab goes straight to the one that takes its place, once: not by way
    // of the first tab, which would show and read its folder. Closing others with the selected
    // one among them goes straight to the nearest that stays, not through each going in turn.
    [Test]
    public async Task ClosingTheSelectedTabGoesStraightToTheNext()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.At("Archive"), fixture.At("Docs"), fixture.At("Source"), fixture.At("Cache"));
        var synced = new List<string>();
        form.Synced += _ => synced.Add(Path.GetFileName(_.CurrentPath));
        TabPage Page(int index) =>
            (TabPage)form.Views.ElementAt(index).Parent!;

        // Cache, the last, selected.
        form.CloseTab(Page(3));
        var closingTheLast = synced.ToList();
        synced.Clear();

        // Docs selected, and closed with Source by Close tabs to the right of Archive.
        ((TabControl)Page(1).Parent!).SelectedTab = Page(1);
        synced.Clear();
        form.CloseTabsToTheRight(Page(0));
        var closingOthers = synced.ToList();

        await Assert.That(closingTheLast).IsEquivalentTo(["Source"]);
        await Assert.That(closingOthers).IsEquivalentTo(["Archive"]);
        await Assert.That(form.Views.Select(_ => Path.GetFileName(_.CurrentPath))).IsEquivalentTo(["Archive"]);
    }

    // A drive plugged in or taken out is in the tree, or gone from it, without F5, as Windows
    // tells every window: once, a little after the last of a burst. A device that isn't a drive,
    // a mouse say, reads nothing.
    [Test]
    public async Task DrivesComeAndGoWithoutRefresh()
    {
        using var fixture = Fixture.Create();
        var drive = Path.GetDirectoryName(fixture.Root)!;
        var drives = new List<string> { drive };
        var places = new Places([], [], ShowComputer: true, _ => "", drives);
        using var form = new QuietForm([fixture.Root], new(), places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        List<string> Drives() =>
            form.Tree.Nodes.Cast<TreeNode>().Last().Nodes.Cast<TreeNode>().Select(_ => (string)_.Tag!).ToList();
        void Tell(int change, int deviceType)
        {
            // DEV_BROADCAST_VOLUME: size, device type, reserved, unit mask, flags.
            var broadcast = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.WriteInt32(broadcast, 0, 18);
                Marshal.WriteInt32(broadcast, 4, deviceType);
                Marshal.WriteInt32(broadcast, 8, 0);
                Marshal.WriteInt32(broadcast, 12, 1 << 25);
                Marshal.WriteInt16(broadcast, 16, 0);
                NativeMethods.SendMessage(form.Handle, 0x0219, change, broadcast);
            }
            finally
            {
                Marshal.FreeHGlobal(broadcast);
            }
        }

        const int arrival = 0x8000;
        const int removed = 0x8004;
        const int volume = 2;
        const int deviceInterface = 5;

        drives.Add(fixture.At("Docs"));
        Tell(arrival, volume);
        Tell(arrival, volume);
        var atOnce = Drives();
        await Pump.Until(() => Drives().Count == 2);

        drives.RemoveAt(1);
        Tell(removed, deviceInterface);
        await Pump.For(TimeSpan.FromMilliseconds(700));
        var notADrive = Drives();
        Tell(removed, volume);
        await Pump.Until(() => Drives().Count == 1);

        await Assert.That(atOnce).IsEquivalentTo([drive]);
        await Assert.That(notADrive).IsEquivalentTo([drive, fixture.At("Docs")]);
    }

    // Ctrl+1 to Ctrl+8 go to the tab at that place, on the top row or the number pad, and Ctrl+9
    // to the last, as in Explorer and the browsers. A number past the last tab does nothing.
    [Test]
    public async Task CtrlAndANumberGoToTheTabThere()
    {
        using var fixture = Fixture.Create();
        using var form = (QuietForm)await Show(fixture, fixture.At("Archive"), fixture.At("Docs"), fixture.At("Source"), fixture.At("Cache"));
        var shown = new List<string>();
        void Press(Keys keys)
        {
            form.Press(keys);
            shown.Add(Path.GetFileName(form.ActiveView!.CurrentPath));
        }

        Press(Keys.Control | Keys.D2);
        Press(Keys.Control | Keys.NumPad3);
        Press(Keys.Control | Keys.D9);
        Press(Keys.Control | Keys.D1);
        Press(Keys.Control | Keys.D5);

        await Assert.That(string.Join(", ", shown)).IsEqualTo("Docs, Source, Cache, Archive, Archive");
    }

    // Twenty tabs from last time ask the shell for each one's icon once, not every tab's again as
    // each opens, two hundred and ten times. Moving between them asks for none again.
    [Test]
    public async Task TabIconsAreAskedForOnceEach()
    {
        using var fixture = Fixture.Create();
        var folders = Enumerable.Range(1, 20)
            .Select(_ => fixture.At($"Tab{_}"))
            .ToList();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
        }

        var settings = new Settings
        {
            Tabs = folders
        };
        var before = ShellIcons.PlaceIconsRead;
        using var form = await Show(fixture, settings);
        var opening = ShellIcons.PlaceIconsRead - before;

        form.ActiveView!.Navigate(fixture.At("Tab2"));
        await Pump.Loaded(form.ActiveView!);
        form.ActiveView!.Navigate(fixture.At("Tab1"));
        await Pump.Loaded(form.ActiveView!);

        await Assert.That(form.Views.Count()).IsEqualTo(20);
        // The tabs', and Pinned's, which is the fixture's folder.
        await Assert.That(opening).IsLessThanOrEqualTo(21);
        await Assert.That(ShellIcons.PlaceIconsRead - before).IsEqualTo(opening);
    }

    // As in Explorer: the folder a tab shows going takes the tab up to the folder it was in,
    // and the tab and the window are named for that.
    [Test]
    public async Task ATabWhoseFolderIsDeletedGoesUp()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.At("Docs"));
        var before = Describe(form);

        Directory.Delete(fixture.At("Docs"), true);
        await Pump.Until(() => form.ActiveView!.CurrentPath == fixture.Root);
        await Pump.Loaded(form.ActiveView!);

        await Verify(
            new
            {
                before,
                after = Describe(form)
            });
    }

    // As in Explorer: the folder a tab shows renamed, the tab follows it, and the tab and the
    // window are named for its new name.
    [Test]
    public async Task ATabWhoseFolderIsRenamedFollowsIt()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.At("Docs"));

        FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");
        await Pump.Until(() => form.ActiveView!.CurrentPath == fixture.At("Documents"));
        await Pump.Loaded(form.ActiveView!);

        await Verify(Describe(form));
    }

    // The buttons that show only an icon, a glyph a screen reader can't say, are named, by what
    // their tooltips say, the keys for them left to their descriptions.
    [Test]
    public async Task IconButtonsAreNamed()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        static IEnumerable<Control> All(Control control) =>
            control.Controls.Cast<Control>().SelectMany(_ => All(_).Prepend(_));

        var buttons = All(form)
            .OfType<GlyphButton>()
            .Select(_ => $"{_.AccessibleName} | {_.AccessibleDescription}")
            .ToList();

        await Assert.That(buttons).IsEquivalentTo(
        [
            "Back | Back (Alt+Left)",
            "Forward | Forward (Alt+Right)",
            "Up | Up (Alt+Up)",
            "Refresh | Refresh (F5)",
            "New tab | New tab (Ctrl+T)",
            "Menu | Menu",
            "Pin Changes | Pin: keep beside the list",
            "Pin History | Pin: keep beside the list",
            "Close | Close (Esc)"
        ]);
    }

    static Task<MainForm> Show(Fixture fixture, params string[] paths) =>
        Show(fixture, new(), paths);

    static async Task<MainForm> Show(Fixture fixture, Settings settings, params string[] paths)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm(paths, settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        foreach (var view in form.Views)
        {
            await Pump.Loaded(view);
        }

        return form;
    }

    static object Describe(MainForm form) =>
        new
        {
            title = form.Text,
            tabs = form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList(),
            active = form.ActiveView?.CurrentPath
        };

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;

        // As the keys would: the modifiers of a real key press come from the keyboard.
        public void Press(Keys keys)
        {
            var message = new Message();
            ProcessCmdKey(ref message, keys);
            Application.DoEvents();
        }
    }
}
