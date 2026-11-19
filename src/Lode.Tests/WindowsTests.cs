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
                File.WriteAllLines(@"C:\Users\simon\AppData\Local\Temp\claude\b4cb8d544a6def5f\599f1b82-1a1b-4412-a890-6c924b123970\scratchpad\names-" + selected + ".txt", commands.Select(_ => _.Name + " | " + _.Action.Target?.GetType().Name).Append("SELECTED " + string.Join(",", form.ActiveView.SelectedEntries().Select(_ => _.FullPath))));
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
    }
}
