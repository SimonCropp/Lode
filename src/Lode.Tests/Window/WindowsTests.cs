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
            })
            .Snapshot(
                """
                {
                  source: {
                    title: Source,
                    tabs: [
                      Source
                    ],
                    active: {TempPath}LodeTests\WindowsTests.MoveTabToAnotherWindow\Projects\Source
                  },
                  target: {
                    title: Deep,
                    tabs: [
                      Projects,
                      Deep
                    ],
                    active: {TempPath}LodeTests\WindowsTests.MoveTabToAnotherWindow\Projects\Source\Deep
                  }
                }
                """);
    }

    // Dropped between two of another window's tabs, a tab goes in there, and is the one shown.
    [Test]
    public async Task MoveTabBetweenAnotherWindowsTabs()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root, fixture.At("Archive"));

        source.MoveTab((TabPage)source.Views.Last().Parent!, target, 1);

        await Assert.That(Titles(target)).IsEquivalentTo(["Projects", "Docs", "Archive"], CollectionOrdering.Matching);
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

        await Assert.That(titles).IsEquivalentTo(["Source", "Projects", "Docs"], CollectionOrdering.Matching);
        await Assert.That(showing.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(shown).IsSameReferenceAs(showing);
        await Assert.That(synced).IsEmpty();
        await Assert.That(settings.Tabs.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["Source", "Projects", "Docs"], CollectionOrdering.Matching);
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
            })
            .Snapshot(
                """
                {
                  sourceClosed: true,
                  target: {
                    title: Docs,
                    tabs: [
                      Projects,
                      Docs
                    ],
                    active: {TempPath}LodeTests\WindowsTests.MovingTheLastTabClosesItsWindow\Projects\Docs
                  }
                }
                """);
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
        await Assert.That(fromTheOneUsedLast).IsEquivalentTo(["Projects", "Source"], CollectionOrdering.Matching);
        await Assert.That(first.IsDisposed).IsTrue();
        await Assert.That(settings.Tabs.Select(_ => Path.GetFileName(_))).IsEquivalentTo(["Projects", "Source"], CollectionOrdering.Matching);
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
                })
                .Snapshot(
                    """
                    {
                      source: {
                        title: Source,
                        tabs: [
                          Source
                        ],
                        active: {TempPath}LodeTests\WindowsTests.MoveTabToNewWindow\Projects\Source
                      },
                      window: {
                        title: Docs,
                        tabs: [
                          Docs
                        ],
                        active: {TempPath}LodeTests\WindowsTests.MoveTabToNewWindow\Projects\Docs
                      },
                      announced: true
                    }
                    """);
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

    // A folder of the address middle-clicked opens in a new tab behind the one showing, which
    // stays where it is, as one middle-clicked in the tree does; Open in new tab, on its
    // right-click menu, does the same.
    [Test]
    public async Task AFolderOfTheAddressOpensInANewTab()
    {
        const int middleButtonDown = 0x0207;
        const int middleButtonUp = 0x0208;
        const int middleButton = 0x0010;
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.At("Source", "Deep"));
        var address = (Breadcrumb)form.Controls.Find("address", true)[0].Parent!;
        var source = address.FolderBounds(fixture.At("Source"));
        var point = ((source.Top + source.Height / 2) << 16) | (source.Left + source.Width / 2);

        NativeMethods.SendMessage(address.Handle, middleButtonDown, middleButton, point);
        NativeMethods.SendMessage(address.Handle, middleButtonUp, 0, point);
        address.FolderCommands(fixture.Root).Single(_ => _?.Id == "openInNewTab")!.Action();
        foreach (var view in form.Views)
        {
            await Pump.Loaded(view);
        }

        await Assert.That(form.Views.Select(_ => _.CurrentPath)).IsEquivalentTo(
            [fixture.At("Source", "Deep"), fixture.At("Source"), fixture.Root],
            CollectionOrdering.Matching);
        await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(fixture.At("Source", "Deep"));
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
            })
            .Snapshot(
                """
                {
                  afterRight: {
                    title: Source,
                    tabs: [
                      Projects,
                      Docs,
                      Source
                    ],
                    active: {TempPath}LodeTests\WindowsTests.CloseTabsToTheLeftAndRight\Projects\Source
                  },
                  afterLeft: {
                    title: Source,
                    tabs: [
                      Source
                    ],
                    active: {TempPath}LodeTests\WindowsTests.CloseTabsToTheLeftAndRight\Projects\Source
                  }
                }
                """);
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
            })
            .Snapshot(
                """
                {
                  twoTabs: {
                    title: Docs\Deep,
                    tabs: [
                      Source\Deep,
                      Docs\Deep
                    ],
                    active: {TempPath}LodeTests\WindowsTests.FoldersWithTheSameNameAreToldApart\Projects\Docs\Deep
                  },
                  twoWindows: {
                    first: {
                      title: Deep,
                      tabs: [
                        Deep
                      ],
                      active: {TempPath}LodeTests\WindowsTests.FoldersWithTheSameNameAreToldApart\Projects\Source\Deep
                    },
                    second: {
                      title: Deep,
                      tabs: [
                        Projects,
                        Deep
                      ],
                      active: {TempPath}LodeTests\WindowsTests.FoldersWithTheSameNameAreToldApart\Projects\Docs\Deep
                    }
                  },
                  movedBack: {
                    title: Docs\Deep,
                    tabs: [
                      Source\Deep,
                      Docs\Deep
                    ],
                    active: {TempPath}LodeTests\WindowsTests.FoldersWithTheSameNameAreToldApart\Projects\Docs\Deep
                  },
                  navigatedAway: {
                    title: Docs,
                    tabs: [
                      Deep,
                      Docs
                    ],
                    active: {TempPath}LodeTests\WindowsTests.FoldersWithTheSameNameAreToldApart\Projects\Docs
                  },
                  oneTab: {
                    title: Deep,
                    tabs: [
                      Deep
                    ],
                    active: {TempPath}LodeTests\WindowsTests.FoldersWithTheSameNameAreToldApart\Projects\Source\Deep
                  }
                }
                """);
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
            })
            .Snapshot(
                """
                {
                  afterClose: {
                    title: Projects,
                    tabs: [
                      Projects
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ReopenClosedTabs\Projects
                  },
                  reopenedOnce: {
                    title: Source,
                    tabs: [
                      Projects,
                      Source
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ReopenClosedTabs\Projects\Source
                  },
                  reopenedTwice: {
                    title: Docs,
                    tabs: [
                      Projects,
                      Source,
                      Docs
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ReopenClosedTabs\Projects\Docs
                  },
                  nothingLeft: true
                }
                """);
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
                })
                .Snapshot(
                    """
                    {
                      off: {
                        title: Source,
                        tabs: [
                          Projects,
                          Docs,
                          Source,
                          Archive
                        ],
                        active: {TempPath}LodeTests\WindowsTests.TabsNotLookedAtCloseAfterTheHoursGiven\Projects\Source
                      },
                      on: {
                        title: Source,
                        tabs: [
                          Source,
                          Archive
                        ],
                        active: {TempPath}LodeTests\WindowsTests.TabsNotLookedAtCloseAfterTheHoursGiven\Projects\Source
                      },
                      reopened: {
                        title: Docs,
                        tabs: [
                          Source,
                          Archive,
                          Docs
                        ],
                        active: {TempPath}LodeTests\WindowsTests.TabsNotLookedAtCloseAfterTheHoursGiven\Projects\Docs
                      },
                      archiveToo: {
                        title: Docs,
                        tabs: [
                          Source,
                          Docs
                        ],
                        active: {TempPath}LodeTests\WindowsTests.TabsNotLookedAtCloseAfterTheHoursGiven\Projects\Docs
                      }
                    }
                    """);
        }
        finally
        {
            MainForm.Time = TimeProvider.System;
        }
    }

    // With Find Results showing, which has no folder, the folder's tab that showed before it
    // stays however long ago it was looked at, as the tree goes to it, rather than every folder's
    // tab closing and leaving Find Results alone in the window. The rest close as they would.
    [Test]
    public async Task TheTabBeforeFindResultsStays()
    {
        using var fixture = Fixture.Create();
        var clock = new Clock(new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        MainForm.Time = clock;
        try
        {
            var settings = new Settings
            {
                CloseUnusedTabs = true,
                UnusedTabHours = 8
            };
            using var form = await Show(fixture, settings, fixture.Root, fixture.At("Docs"), fixture.At("Source"));

            // 10:00, Docs looked at, then Find Results opened over it.
            clock.Now = clock.Now.AddHours(1);
            var docs = form.Views.ElementAt(1);
            Pick(docs);
            await Pump.Loaded(docs);
            form.FindHost.Open();
            form.FindHost.Panel.FindBox.Text = "alpha";
            form.FindHost.Find();
            await form.FindHost.ResultsView!.Searching;

            // 19:00: every folder's tab 9 hours or more unused.
            clock.Now = clock.Now.AddHours(9);
            form.CloseUnusedTabs();
            var closed = Describe(form);

            form.CloseTab((TabPage)form.FindHost.ResultsView!.Parent!);

            await Verify(
                new
                {
                    closed,
                    findResultsClosed = Describe(form)
                })
                .Snapshot(
                    """
                    {
                      closed: {
                        title: Find Results,
                        tabs: [
                          Docs
                        ]
                      },
                      findResultsClosed: {
                        title: Docs,
                        tabs: [
                          Docs
                        ],
                        active: {TempPath}LodeTests\WindowsTests.TheTabBeforeFindResultsStays\Projects\Docs
                      }
                    }
                    """);
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
                })
                .Snapshot(
                    """
                    {
                      restored: {
                        title: Source,
                        tabs: [
                          Docs,
                          Source
                        ],
                        active: {TempPath}LodeTests\WindowsTests.RestoredTabsAreAsUnusedAsTheyWere\Projects\Source
                      },
                      saved: [
                        Docs 05:00,
                        Source 10:00
                      ]
                    }
                    """);
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
            })
            .Snapshot(
                """
                {
                  front: recent,
                  recent: {
                    title: Deep,
                    tabs: [
                      Archive,
                      Deep
                    ],
                    active: {TempPath}LodeTests\WindowsTests.LaunchedFoldersOpenAsTabs\Projects\Source\Deep
                  },
                  older: {
                    title: Docs,
                    tabs: [
                      Projects,
                      Docs,
                      Source
                    ],
                    active: {TempPath}LodeTests\WindowsTests.LaunchedFoldersOpenAsTabs\Projects\Docs
                  }
                }
                """);
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
            })
            .Snapshot(
                """
                {
                  front: older,
                  recent: {
                    title: Archive,
                    tabs: [
                      Archive
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ALaunchedFileIsSelectedInTheTabShowingItsFolder\Projects\Archive
                  },
                  older: {
                    title: Source,
                    tabs: [
                      Source,
                      Projects
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ALaunchedFileIsSelectedInTheTabShowingItsFolder\Projects\Source
                  },
                  selected: [
                    main.aleph
                  ],
                  canGoBack: false
                }
                """);
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
        var nothingSelected = form.ActionsBar.Describe().ToList();
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);
        form.ActionsBar.Update();

        await Verify(
            new
            {
                nothingSelected,
                fileSelected = form.ActionsBar.Describe().ToList()
            })
            .Snapshot(
                """
                {
                  nothingSelected: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Cut (disabled),
                    Copy (disabled),
                    Paste,
                    Rename (disabled),
                    Delete (disabled),
                    ----,
                    Edit (disabled),
                    Open with (disabled),
                    Terminal,
                    Terminal (admin),
                    AI,
                    ----,
                    Compress to ZIP file (disabled),
                    Copy as path,
                    Properties,
                    ----,
                    Git (disabled),
                    ----,
                    See more
                  ],
                  fileSelected: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Cut,
                    Copy,
                    Paste,
                    Rename,
                    Delete,
                    ----,
                    Edit (disabled),
                    Open with,
                    Terminal,
                    Terminal (admin),
                    AI,
                    ----,
                    Compress to ZIP file,
                    Copy as path,
                    Properties,
                    ----,
                    Git (disabled),
                    ----,
                    See more
                  ]
                }
                """);
    }

    // A button showing only its icon has its name as a tooltip, with its keys. A drop-down shows
    // its name beside its icon, so has no tooltip, rather than its name again.
    [Test]
    public async Task OnlyButtonsWithoutTheirNameShownHaveATooltip()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);

        await Verify(
                new[] { "New", "Open with", "AI", "Git", "Copy", "Terminal", "See more" }
                    .Select(_ => $"{_}: {form.ActionsBar.Item(_).ToolTipText ?? "none"}"))
            .Snapshot(
                """
                [
                  New: none,
                  Open with: none,
                  AI: none,
                  Git: none,
                  Copy: Copy (Ctrl+C),
                  Terminal: Terminal,
                  See more: See more
                ]
                """);
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
            })
            .Snapshot(
                """
                {
                  filter: {
                    insideFrame: true,
                    gapAbove: 3,
                    gapBelow: 4
                  },
                  address: {
                    insideFrame: true,
                    gapAbove: 3,
                    gapBelow: 4
                  }
                }
                """);
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
        // Room for the two buttons added, so neither goes under See more.
        settings.SetPlacement("openWith", button: false, menu: true);
        settings.SetPlacement(LodeActions.More, button: true, menu: true);
        using var form = await Show(fixture, settings, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("Docs"));
        await Pump.Loaded(form.ActiveView);
        form.ActionsBar.Update();
        var buttons = form.ActionsBar.Describe().ToList();

        form.ActionsBar.Run("openInNewTab");
        await Pump.Loaded(form.Views.Last());

        await Verify(
            new
            {
                buttons,
                tabs = form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList()
            })
            .Snapshot(
                """
                {
                  buttons: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Copy,
                    Paste,
                    Rename,
                    Delete,
                    ----,
                    Open in new tab,
                    Edit (disabled),
                    Terminal,
                    Terminal (admin),
                    AI,
                    ----,
                    Compress to ZIP file,
                    Copy as path,
                    Properties,
                    ----,
                    Git (disabled),
                    ----,
                    Show more options,
                    ----,
                    See more
                  ],
                  tabs: [
                    Projects,
                    Docs
                  ]
                }
                """);
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
        form.ActionsBar.Update();

        await Verify(
            new
            {
                buttons = form.ActionsBar.Describe().ToList(),
                clipboard = form.ActionsBar.ParentEntries("Clipboard").Select(DescribeCommand).ToList()
            })
            .Snapshot(
                """
                {
                  buttons: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Clipboard,
                    Paste,
                    Rename,
                    Delete,
                    ----,
                    Edit (disabled),
                    Open with (disabled),
                    Terminal,
                    Terminal (admin),
                    AI,
                    ----,
                    Compress to ZIP file,
                    Properties,
                    ----,
                    Git (disabled),
                    ----,
                    See more
                  ],
                  clipboard: [
                    Cut,
                    Copy,
                    Copy as path
                  ]
                }
                """);
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
        form.ActionsBar.Update();

        await Verify(
            new
            {
                buttons = form.ActionsBar.Describe().ToList(),
                git = form.ActionsBar.ParentEntries("Git").Select(DescribeCommand).ToList()
            })
            .Snapshot(
                """
                {
                  buttons: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Cut,
                    Copy,
                    Paste,
                    Rename,
                    Delete,
                    ----,
                    Edit (disabled),
                    Open with (disabled),
                    Terminal,
                    Terminal (admin),
                    AI,
                    ----,
                    Compress to ZIP file,
                    Copy as path,
                    Properties,
                    ----,
                    Git,
                    ----,
                    See more
                  ],
                  git: [
                    Fetch,
                    Pull,
                    Sync fork,
                    Switch branch…,
                    Switch to default branch,
                    Update from default branch,
                    Merge into current branch… (disabled),
                    Merge current branch into…,
                    Squash and merge into current branch… (disabled),
                    Rebase current branch… (disabled),
                    Push…,
                    Tag… (disabled),
                    Discard…,
                    Purge…
                  ]
                }
                """);
    }

    // Right-clicking an action's button nests it under its parent, in a drop-down where it was,
    // which takes in the next nested, or hides it, under See more. Where it is in the right-click
    // menu stays as it was.
    [Test]
    public async Task RightClickingAButtonNestsOrHidesIt()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        using var form = await Show(fixture, settings, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);
        form.ActionsBar.Update();
        var renameMenu = form.ActionsBar.MenuCommands("Rename").Select(DescribeCommand).ToList();

        Choose(form.ActionsBar.MenuCommands("Rename"), "Nest under File");
        var fileMenu = form.ActionsBar.MenuCommands("File").Select(DescribeCommand).ToList();
        Choose(form.ActionsBar.MenuCommands("Delete"), "Nest under File");
        Choose(form.ActionsBar.MenuCommands("Copy"), "Hide button");

        await Verify(
                new
                {
                    renameMenu,
                    fileMenu,
                    buttons = form.ActionsBar.Describe().ToList(),
                    file = form.ActionsBar.ParentEntries("File").Select(DescribeCommand).ToList(),
                    copyUnderSeeMore = form.ActionsBar.OverflowCommands(Point.Empty).Any(_ => _?.Text == "Copy"),
                    copyInMenu = settings.ShowsInMenu("copy"),
                    renameNestedInMenu = settings.NestsInMenu("rename")
                })
            .Snapshot(
                """
                {
                  renameMenu: [
                    Hide button,
                    Nest under File,
                    ----,
                    Customize buttons…
                  ],
                  fileMenu: [
                    Hide button,
                    Un-nest,
                    ----,
                    Customize buttons…
                  ],
                  buttons: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Cut,
                    Paste,
                    File,
                    ----,
                    Edit (disabled),
                    Open with,
                    Terminal,
                    Terminal (admin),
                    AI,
                    ----,
                    Compress to ZIP file,
                    Copy as path,
                    Properties,
                    ----,
                    Git (disabled),
                    ----,
                    See more
                  ],
                  file: [
                    Rename,
                    Delete
                  ],
                  copyUnderSeeMore: true,
                  copyInMenu: true,
                  renameNestedInMenu: false
                }
                """);
    }

    // Right-clicking a parent's drop-down un-nests one of what is in it, a button of its own
    // after the drop-down, or hides the drop-down, the rest going under See more, still nested
    // there for when they are buttons again.
    [Test]
    public async Task RightClickingADropDownUnnestsOneOrHidesIt()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        // Room for the button un-nested, so it doesn't go under See more.
        settings.SetPlacement("openWith", button: false, menu: true);
        using var form = await Show(fixture, settings, fixture.Root);
        var gitMenu = form.ActionsBar.MenuCommands("Git");

        Choose(gitMenu.Single(_ => _?.Text == "Un-nest one")!.Children!, "Git pull");
        var unnested = form.ActionsBar.Describe().ToList();
        Choose(form.ActionsBar.MenuCommands("Git"), "Hide button");

        await Verify(
                new
                {
                    gitMenu = gitMenu.Select(DescribeCommand).ToList(),
                    unnestOne = gitMenu.Single(_ => _?.Text == "Un-nest one")!.Children!.Select(DescribeCommand).ToList(),
                    unnested = unnested.SkipWhile(_ => _ != "Properties").ToList(),
                    hidden = form.ActionsBar.Describe().SkipWhile(_ => _ != "Properties").ToList(),
                    fetch = new
                    {
                        button = settings.ShowsAsButton("gitFetch"),
                        nested = settings.NestsButton("gitFetch"),
                        nestedInMenu = settings.NestsInMenu("gitFetch")
                    },
                    pull = new
                    {
                        button = settings.ShowsAsButton("gitPull"),
                        nested = settings.NestsButton("gitPull"),
                        nestedInMenu = settings.NestsInMenu("gitPull")
                    }
                })
            .Snapshot(
                """
                {
                  gitMenu: [
                    Hide button,
                    Un-nest all,
                    Un-nest one,
                    ----,
                    Customize buttons…
                  ],
                  unnestOne: [
                    Git fetch,
                    Git pull,
                    Git sync fork,
                    Git switch branch,
                    Git switch to default branch,
                    Git update from default branch,
                    Git merge into current branch,
                    Git merge current branch into,
                    Git squash and merge into current branch,
                    Git rebase current branch,
                    Git push,
                    Git tag,
                    Git discard,
                    Git purge
                  ],
                  unnested: [
                    Properties,
                    ----,
                    Git (disabled),
                    Git pull (disabled),
                    ----,
                    See more
                  ],
                  hidden: [
                    Properties,
                    ----,
                    Git pull (disabled),
                    ----,
                    See more
                  ],
                  fetch: {
                    button: false,
                    nested: true,
                    nestedInMenu: true
                  },
                  pull: {
                    button: true,
                    nested: false,
                    nestedInMenu: true
                  }
                }
                """);
    }

    // Un-nest all puts each of a drop-down's actions back as a button of its own, where it was.
    [Test]
    public async Task RightClickingADropDownUnnestsAll()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        foreach (var id in new[] { "cut", "copy" })
        {
            settings.SetPlacement(id, button: true, menu: true, buttonNested: true);
        }

        using var form = await Show(fixture, settings, fixture.Root);
        var nested = form.ActionsBar.Describe().ToList();

        Choose(form.ActionsBar.MenuCommands("Clipboard"), "Un-nest all");

        await Verify(
                new
                {
                    nested = nested.Take(6).ToList(),
                    unnested = form.ActionsBar.Describe().Take(7).ToList()
                })
            .Snapshot(
                """
                {
                  nested: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Clipboard (disabled),
                    Paste
                  ],
                  unnested: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Cut (disabled),
                    Copy (disabled),
                    Paste
                  ]
                }
                """);
    }

    // Right-clicking an action in its parent's drop-down un-nests it, a button of its own in its
    // place among the others, or hides it, under See more, still nested there for when it is a
    // button again. With Fetch hidden, the drop-down stands where Sync fork does, after Pull.
    // Outside the code folder none of them applies, and greyed out they are still called what
    // the Git submenu calls them.
    [Test]
    public async Task RightClickingANestedActionUnnestsOrHidesIt()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        // Room for the button un-nested, so it doesn't go under See more.
        settings.SetPlacement("openWith", button: false, menu: true);
        using var form = await Show(fixture, settings, fixture.Root);
        var pullMenu = form.ActionsBar.NestedMenuCommands("gitPull").Select(DescribeCommand).ToList();

        Choose(form.ActionsBar.NestedMenuCommands("gitPull"), "Un-nest");
        Choose(form.ActionsBar.NestedMenuCommands("gitFetch"), "Hide button");

        await Verify(
                new
                {
                    pullMenu,
                    buttons = form.ActionsBar.Describe().SkipWhile(_ => _ != "Properties").ToList(),
                    git = form.ActionsBar.ParentEntries("Git").Select(_ => _!.Text).ToList(),
                    fetch = new
                    {
                        button = settings.ShowsAsButton("gitFetch"),
                        nested = settings.NestsButton("gitFetch")
                    }
                })
            .Snapshot(
                """
                {
                  pullMenu: [
                    Hide button,
                    Un-nest,
                    ----,
                    Customize buttons…
                  ],
                  buttons: [
                    Properties,
                    ----,
                    Git pull (disabled),
                    Git (disabled),
                    ----,
                    See more
                  ],
                  git: [
                    Sync fork,
                    Switch branch…,
                    Switch to default branch,
                    Update from default branch,
                    Merge into current branch…,
                    Merge current branch into…,
                    Squash and merge into current branch…,
                    Rebase current branch…,
                    Push…,
                    Tag…,
                    Discard…,
                    Purge…
                  ],
                  fetch: {
                    button: false,
                    nested: true
                  }
                }
                """);
    }

    // An action's menu in its parent's drop-down opens where it was right-clicked, as a submenu
    // of the drop-down, which stays open beneath it, as Windows' menus do, and Escape closes
    // only the menu. Once it has closed the drop-down lists what it did.
    [Test]
    public async Task RightClickingANestedActionKeepsTheDropDownOpen()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        var git = (ToolStripDropDownItem)form.ActionsBar.Item("Git");
        git.ShowDropDown();
        Application.DoEvents();
        var listed = git.DropDownItems.Cast<ToolStripItem>().Select(_ => _.Text).ToList();
        var pull = git.DropDownItems.Cast<ToolStripItem>().Single(_ => _.Text == "Pull");

        var pointer = RightClick(git.DropDown, pull);
        var menu = form.ActionsBar.MenuShowing;
        var whileOpen = new
        {
            gitOpen = git.DropDown.Visible,
            menuOpen = menu?.Visible,
            atPointer = menu?.Bounds.Location == git.DropDown.PointToScreen(pointer),
            entries = menu?.Items.Cast<ToolStripItem>().Select(_ => _.Text).ToList()
        };
        menu!.Close(ToolStripDropDownCloseReason.Keyboard);
        var openAfterEscape = git.DropDown.Visible;
        await Pump.Until(() => form.ActionsBar.MenuShowing == null);
        var listedAfter = git.DropDownItems.Cast<ToolStripItem>().Select(_ => _.Text).ToList();
        git.HideDropDown();

        await Assert.That(listedAfter).IsEquivalentTo(listed, CollectionOrdering.Matching);
        await Verify(
                new
                {
                    whileOpen,
                    openAfterEscape
                })
            .Snapshot(
                """
                {
                  whileOpen: {
                    gitOpen: true,
                    menuOpen: true,
                    atPointer: true,
                    entries: [
                      Hide button,
                      Un-nest,
                      ,
                      Customize buttons…
                    ]
                  },
                  openAfterEscape: true
                }
                """);
    }

    // Right-clicks the item, returning where, in the strip.
    static Point RightClick(ToolStrip strip, ToolStripItem item)
    {
        const int rightButtonDown = 0x0204;
        const int rightButtonUp = 0x0205;
        var point = new Point(item.Bounds.Left + 10, item.Bounds.Top + 5);
        var packed = (point.Y << 16) | (point.X & 0xFFFF);
        NativeMethods.SendMessage(strip.Handle, rightButtonDown, 2, packed);
        NativeMethods.SendMessage(strip.Handle, rightButtonUp, 0, packed);
        return point;
    }

    // Right-clicking anywhere on the bar but a button, See more say, offers only the Button
    // actions page of Options.
    [Test]
    public async Task RightClickingSeeMore()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);

        await Assert.That(form.ActionsBar.MenuCommands("See more").Select(DescribeCommand)).IsEquivalentTo(["Customize buttons…"]);
    }

    static void Choose(IEnumerable<QuickMenu.Command?> commands, string text) =>
        commands.Single(_ => _?.Text == text)!.Action();

    // See more, at the end of the bar, lists the actions that are not buttons, greyed where
    // they do not apply: here, to a file.
    [Test]
    public async Task SeeMoreListsTheOtherActions()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);

        await Verify(form.ActionsBar.OverflowCommands(Point.Empty).Select(DescribeCommand))
            .Snapshot(
                """
                [
                  Sort by,
                  ----,
                  Open,
                  Open in new tab (disabled),
                  Run (disabled),
                  ----,
                  Pin to Quick access (disabled),
                  Create shortcut,
                  Extract All (disabled),
                  Extract here (disabled),
                  ----,
                  Update NuGets (disabled),
                  Update NuGets and build (disabled),
                  ----,
                  Show more options
                ]
                """);
    }

    // In a window too narrow for every button, those that don't fit go under See more, from the
    // end of the bar, in their places among the actions that aren't buttons, and come back as
    // the window widens.
    [Test]
    public async Task ButtonsThatDontFitAreUnderSeeMore()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);
        form.ActionsBar.Update();
        var wide = form.ActionsBar.Describe().ToList();
        var width = form.Width;

        form.Width = 700;
        var buttons = form.ActionsBar.Describe().ToList();
        var seeMore = form.ActionsBar.OverflowCommands(Point.Empty).Select(DescribeCommand).ToList();
        Widen(form, width);

        await Assert.That(form.ActionsBar.Describe().ToList()).IsEquivalentTo(wide);
        await Verify(
            new
            {
                buttons,
                seeMore
            })
            .Snapshot(
                """
                {
                  buttons: [
                    New,
                    ----,
                    Undo (disabled),
                    Redo (disabled),
                    Cut,
                    Copy,
                    Paste,
                    Rename,
                    Delete,
                    ----,
                    Edit (disabled),
                    ----,
                    See more
                  ],
                  seeMore: [
                    Sort by,
                    ----,
                    Open,
                    Open in new tab (disabled),
                    Run (disabled),
                    Open with,
                    Terminal,
                    Terminal (admin),
                    Open in Claude,
                    Coding agent,
                    ----,
                    Pin to Quick access (disabled),
                    Create shortcut,
                    Compress to ZIP file,
                    Extract All (disabled),
                    Extract here (disabled),
                    Copy as path,
                    Properties,
                    ----,
                    Git fetch (disabled),
                    Git pull (disabled),
                    Git sync fork (disabled),
                    Git switch branch (disabled),
                    Git switch to default branch (disabled),
                    Git update from default branch (disabled),
                    Git merge into current branch (disabled),
                    Git merge current branch into (disabled),
                    Git squash and merge into current branch (disabled),
                    Git rebase current branch (disabled),
                    Git push (disabled),
                    Git tag (disabled),
                    Git discard (disabled),
                    Git purge (disabled),
                    Update NuGets (disabled),
                    Update NuGets and build (disabled),
                    ----,
                    Show more options
                  ]
                }
                """);
    }

    // Once a window is open, WinForms keeps it no wider than the screen, maximum size or not, so
    // on CI's 1024 wide screen setting Width back to what Show opened it at gives one too narrow
    // for every button.
    static void Widen(Form form, int width) =>
        WindowSize.Set(form, width, form.Height);

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
                .. form.ActionsBar.OverflowCommands(Point.Empty).Select(DescribeCommand),
                .. form.Commands.Palette().Select(_ => _.Name)
            ];

            await Assert.That(offered.Where(_ => _.Contains("Claude") || _.Contains("Coding agent"))).IsEmpty();
        }
        finally
        {
            ClaudeDesktop.IsInstalled = claude;
            CodingAgents.Installed = agents;
        }
    }

    // The palette sorts the list by any column, a hidden one too, with something selected as
    // with nothing, which the right-click menu only offers for.
    [Test]
    public async Task PaletteSorts()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings
        {
            HiddenColumns = ["Size"]
        };
        using var form = await Show(fixture, settings, fixture.Root);
        var view = form.ActiveView!;
        var nothingSelected = form.Commands.Palette().Where(_ => _.Name.StartsWith("Sort by: ", StringComparison.Ordinal)).Select(_ => _.Name).ToList();
        view.Reveal(fixture.At("Docs"));
        await Pump.Loaded(view);

        form.Commands.Palette().Single(_ => _.Name == "Sort by: Size").Action();

        await Assert.That(nothingSelected).IsEquivalentTo(["Sort by: Name", "Sort by: Date modified", "Sort by: Type", "Sort by: Size", "Sort by: Ascending", "Sort by: Descending"], CollectionOrdering.Matching);
        await Assert.That(view.Sort).IsEqualTo((6, true));
        await Assert.That(view.ColumnTitles).Contains("Size");
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
                var commands = form.Commands.Palette();
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
                })
                .Snapshot(
                    """
                    {
                      folder: [
                        terminal Projects/Docs,
                        admin terminal Projects/Docs,
                        claude://code/new?folder={Root}%5CDocs,
                        terminal Projects/Docs C:\Tools\agent.exe
                      ],
                      file: [
                        terminal Projects,
                        admin terminal Projects,
                        claude://code/new?folder={Root},
                        terminal Projects C:\Tools\agent.exe
                      ]
                    }
                    """);
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

        await Verify(form.Commands.Menu().Select(DescribeCommand))
            .Snapshot(
                """
                [
                  New tab,
                  New window,
                  Reopen closed tab (disabled),
                  Command palette,
                  Find in files,
                  Clone a repository…,
                  Open NuGet cache,
                  Purge NuGet cache,
                  ----,
                  Show hidden items,
                  Show system files,
                  Unpin this folder from Quick access,
                  ----,
                  Options…,
                  About Lode,
                  ----,
                  Exit Lode
                ]
                """);
    }

    // Purge NuGet cache, from the ⋯ menu, runs in the Actions panel, as Update NuGets does,
    // naming where restore puts packages. Dotnet is replaced, so this machine's caches are left
    // alone.
    [Test]
    public async Task PurgeNugetCacheFromTheMenu()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        var run = NuGetCache.Run;
        var started = new List<string>();
        NuGetCache.Run = startInfo =>
        {
            started.Add(string.Join(" ", startInfo.ArgumentList));
            return (0, "Local resources cleared.");
        };
        ActionLog.Reset();
        try
        {
            form.Commands.Menu().Single(_ => _?.Text == "Purge NuGet cache")!.Action();
            await Pump.Until(() => ActionLog.Entries is [{ State: not ActionLog.State.Running }]);

            var entry = ActionLog.Entries.Single();
            await Assert.That(entry.Title).IsEqualTo("Purge NuGet cache");
            await Assert.That(entry.Where).IsEqualTo(NuGetCache.PackagesFolder());
            await Assert.That(entry.Summary).IsEqualTo("Cleared");
            await Assert.That(started).IsEquivalentTo(["nuget locals all --clear"]);
        }
        finally
        {
            NuGetCache.Run = run;
            ActionLog.Reset();
        }
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
            })
            .Snapshot(
                """
                {
                  pinned: [
                    Duplicate tab,
                    Unpin from Quick access,
                    ----,
                    Close tab,
                    Close other tabs,
                    Close tabs to the left (disabled),
                    Close tabs to the right,
                    Close all tabs,
                    ----,
                    Reopen closed tab (disabled)
                  ],
                  notPinned: [
                    Duplicate tab,
                    Pin to Quick access,
                    ----,
                    Close tab,
                    Close other tabs,
                    Close tabs to the left,
                    Close tabs to the right (disabled),
                    Close all tabs,
                    ----,
                    Reopen closed tab (disabled)
                  ]
                }
                """);
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
        var commands = form.Commands.Palette()
            .Where(_ => !_.Name.StartsWith("New: ") || _.Name == "New: Folder")
            .Select(_ => _.Shortcut.Length == 0 ? _.Name : $"{_.Name} ({_.Shortcut})");
        await Verify(commands)
            .Snapshot(
                """
                [
                  Up (Alt+Up),
                  Refresh (F5),
                  Go to address (Ctrl+L),
                  Search this folder (Ctrl+F),
                  New tab (Ctrl+T),
                  New window (Ctrl+N),
                  Find in files (Ctrl+Shift+F),
                  Clone a repository… (Ctrl+Shift+O),
                  Open NuGet cache,
                  Purge NuGet cache,
                  Duplicate tab,
                  Close tab (Ctrl+W),
                  Close other tabs,
                  Close tabs to the left,
                  Close all tabs,
                  Next tab (Ctrl+Tab),
                  Previous tab (Ctrl+Shift+Tab),
                  Hide hidden items (Ctrl+H),
                  Show system files,
                  Terminal,
                  Terminal (admin),
                  Open in Claude,
                  Coding agent: Claude Code,
                  Coding agent: Codex CLI,
                  Pin this folder to Quick access,
                  Sort by: Name,
                  Sort by: Date modified,
                  Sort by: Type,
                  Sort by: Size,
                  Sort by: Ascending,
                  Sort by: Descending,
                  New: Folder (Ctrl+Shift+N),
                  Copy as path,
                  Properties (Alt+Enter),
                  Options…,
                  About Lode,
                  Exit Lode
                ]
                """);
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
            CollectionOrdering.Matching);
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
            CollectionOrdering.Matching);
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
            })
            .Snapshot(
                """
                {
                  before: {
                    title: Docs,
                    tabs: [
                      Docs
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ATabWhoseFolderIsDeletedGoesUp\Projects\Docs
                  },
                  after: {
                    title: Projects,
                    tabs: [
                      Projects
                    ],
                    active: {TempPath}LodeTests\WindowsTests.ATabWhoseFolderIsDeletedGoesUp\Projects
                  }
                }
                """);
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

        await Verify(Describe(form))
            .Snapshot(
                """
                {
                  title: Documents,
                  tabs: [
                    Documents
                  ],
                  active: {TempPath}LodeTests\WindowsTests.ATabWhoseFolderIsRenamedFollowsIt\Projects\Documents
                }
                """);
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
            "Filter changed files | Filter the files by name",
            "Add a description | Add a description",
            "Copy the full SHA | Copy the full SHA",
            "Close | Close (Esc)"
        ]);
    }

    // A repository cloned from the Clone dialog opens in a tab of its own once it is there, the
    // tab showing, and the tab open before stays.
    [Test]
    public async Task CloneOpensTheCloneInANewTab()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        using var fixture = Fixture.Create();
        // In the scratch folder, which takes git's read-only objects away, as the fixture can't.
        var destination = code.At("Cloned");
        var list = HostedRepos.ListGitHub;
        HostedRepos.ListGitHub = _ => Task.FromResult(
            new HostedListing(
                [new(HostedRepos.GitHub, HostedRepos.YourRepositories, "octo/Repo", code.Origin("Repo"), "https://github.com/octo/Repo", false, false)],
                []));
        CloneDialog.Forget();
        try
        {
            using var form = await Show(fixture, new Settings { CodeDirectory = fixture.Root }, fixture.Root);
            form.Commands.ShowCloneDialog = dialog =>
            {
                dialog.ShowInTaskbar = false;
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new(-20000, -20000);
                dialog.Show();
                var waited = Stopwatch.StartNew();
                while (!(dialog.Rows.Count > 1 && dialog.Loaded.IsCompleted) &&
                       waited.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }

                dialog.List.SelectedIndex = 1;
                dialog.PathBox.Text = destination;
                dialog.CloneButton.PerformClick();
                return dialog.DialogResult;
            };

            form.Commands.ShowClone();
            await Pump.Until(() => form.Views.Count() == 2);

            await Assert.That(Directory.Exists(Path.Combine(destination, ".git"))).IsTrue();
            await Assert.That(form.Views.Select(_ => _.CurrentPath).ToList()).IsEquivalentTo(new List<string> { fixture.Root, destination }, CollectionOrdering.Matching);
            await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(destination);
        }
        finally
        {
            HostedRepos.ListGitHub = list;
        }
    }

    // The status bar has the most recent tag right after the branch, a dot between, and the
    // commits ahead after it. A repository with no tag has none, nor its dot.
    [Test]
    public async Task StatusBarShowsTheMostRecentTag()
    {
        using var stubs = new PaneStubs();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);

        await Assert.That(await Shown("0.25.0")).IsEquivalentTo(["main", "·", "0.25.0", "·", "1 ahead"], CollectionOrdering.Matching);
        await Assert.That(await Shown(null)).IsEquivalentTo(["main", "·", "1 ahead"], CollectionOrdering.Matching);

        // The git items the bar shows, in order.
        async Task<List<string>> Shown(string? tag)
        {
            GitCache.Reset();
            FolderView.ReadGit = (_, _) => new("main", PaneStubs.History[0].Sha, 1, 0, [])
            {
                UpstreamName = "origin/main",
                Tag = tag
            };
            using var form = await Show(fixture, settings, fixture.Root);
            await PaneStubs.Settled(form);
            var items = form.StatusBar.Strip.Items.Cast<ToolStripItem>().ToList();
            var start = items.IndexOf(form.StatusBar.Strip.Items["git"]!);
            var end = items.IndexOf(form.StatusBar.Strip.Items["site"]!);
            return items[start..end]
                .Where(_ => _.Available)
                .Select(_ => _.Text ?? "")
                .ToList();
        }
    }

    // Right-clicked, the branch on the status bar offers to copy its name, to view it on the
    // site origin is on, and to create a pull request from it there, and the tag to copy and view
    // its own. Detached, the branch's place offers the commit, and a branch tracking none, not on
    // the site yet, its name, and to create a pull request once it is pushed.
    [Test]
    public async Task StatusBarMenusCopyAndViewTheBranchAndTag()
    {
        using var stubs = new PaneStubs();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        await File.WriteAllTextAsync(fixture.At(".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/octo/Lode.git\n");
        var sha = PaneStubs.History[0].Sha;
        var done = new List<string>();
        var setClipboardText = FolderView.SetClipboardText;
        FolderView.SetClipboardText = _ => done.Add($"copied {_}");
        var ask = GitStatusActions.Ask;
        GitStatusActions.Ask = (_, question) =>
        {
            done.Add($"asked {question.Heading}");
            return null;
        };
        try
        {
            await Assert.That(await Run("main", "origin/main", "git")).IsEquivalentTo(
                ["Copy branch name: copied main", "View on GitHub: opened https://github.com/octo/Lode/tree/main", "Create pull request: opened https://github.com/octo/Lode/compare/main?expand=1"],
                CollectionOrdering.Matching);
            await Assert.That(await Run("main", "origin/main", "gitTag")).IsEquivalentTo(
                ["Copy tag name: copied 0.25.0", "View on GitHub: opened https://github.com/octo/Lode/releases/tag/0.25.0"],
                CollectionOrdering.Matching);
            await Assert.That(await Run(null, null, "git")).IsEquivalentTo(
                [$"Copy SHA: copied {sha}", $"View on GitHub: opened https://github.com/octo/Lode/commit/{sha}"],
                CollectionOrdering.Matching);
            await Assert.That(await Run("local-only", null, "git")).IsEquivalentTo(
                ["Copy branch name: copied local-only", "Create pull request…: asked local-only isn't on origin yet"],
                CollectionOrdering.Matching);
        }
        finally
        {
            FolderView.SetClipboardText = setClipboardText;
            GitStatusActions.Ask = ask;
        }

        // Each entry of the item's right-click menu, run, with what it did.
        async Task<List<string>> Run(string? branch, string? upstream, string item)
        {
            GitCache.Reset();
            FolderView.ReadGit = (_, _) => new(branch, sha, 0, 0, [])
            {
                UpstreamName = upstream,
                Tag = "0.25.0"
            };
            using var form = await Show(fixture, settings, fixture.Root);
            await PaneStubs.Settled(form);
            form.OpenSite = _ => done.Add($"opened {_}");
            var ran = new List<string>();
            foreach (var command in form.StatusBar.CommandsFor(form.StatusBar.Strip.Items[item]!))
            {
                command!.Action();
                ran.Add($"{command.Text}: {done[^1]}");
            }

            return ran;
        }
    }

    // The branch is viewed on the site as origin has it, by the name it has there: one tracking
    // origin's main under a name of its own is viewed at main. One whose branch on origin has
    // gone, merged and deleted say, one tracking a branch of the repository's own, and one
    // tracking another remote's, as a fork's tracks upstream's, aren't on origin as they are, so
    // offer only their names. Create pull request, and Ctrl+R, open the page that creates one
    // from the branch origin has, wherever it is viewed there; one tracking none asks to push it
    // first, and Ctrl+R on the others says why there is none to create.
    [Test]
    public async Task StatusBarViewsTheBranchOriginHas()
    {
        using var stubs = new PaneStubs();
        using var fixture = Fixture.Create();
        var settings = PaneStubs.InRepository(fixture);
        await File.WriteAllTextAsync(fixture.At(".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/octo/Lode.git\n");
        using var code = new CodeCommandsTests.Scratch();
        var fork = code.CreateFork("Repo");
        CodeCommandsTests.Scratch.Git(fork, "switch", "-c", "feature/x");
        CodeCommandsTests.Scratch.Git(fork, "push", "-u", "origin", "feature/x");
        CodeCommandsTests.Scratch.Git(fork, "switch", "-c", "fix", "--track", "origin/main");
        CodeCommandsTests.Scratch.Git(fork, "switch", "-c", "local", "--track", "main");
        CodeCommandsTests.Scratch.Git(fork, "switch", "-c", "synced", "--track", "upstream/main");
        CodeCommandsTests.Scratch.Git(fork, "switch", "-c", "unpushed");
        CodeCommandsTests.Scratch.Git(fork, "switch", "-c", "merged");
        CodeCommandsTests.Scratch.Git(fork, "push", "-u", "origin", "merged");
        CodeCommandsTests.Scratch.Git(fork, "push", "origin", "--delete", "merged");
        CodeCommandsTests.Scratch.Git(fork, "fetch", "--prune", "origin");
        var done = new List<string>();
        var setClipboardText = FolderView.SetClipboardText;
        FolderView.SetClipboardText = _ => done.Add($"copied {_}");
        var ask = GitStatusActions.Ask;
        GitStatusActions.Ask = (_, question) =>
        {
            done.Add($"asked {question.Heading}");
            return null;
        };
        try
        {
            await Verify(
                new
                {
                    main = await Run("main"),
                    sameName = await Run("feature/x"),
                    otherName = await Run("fix"),
                    local = await Run("local"),
                    otherRemote = await Run("synced"),
                    unpushed = await Run("unpushed"),
                    gone = await Run("merged")
                })
                .Snapshot(
                    """
                    {
                      main: [
                        Copy branch name: copied main,
                        View on GitHub: opened https://github.com/octo/Lode/tree/main,
                        Create pull request: opened https://github.com/octo/Lode/compare/main?expand=1,
                        Ctrl+R: opened https://github.com/octo/Lode/compare/main?expand=1
                      ],
                      sameName: [
                        Copy branch name: copied feature/x,
                        View on GitHub: opened https://github.com/octo/Lode/tree/feature/x,
                        Create pull request: opened https://github.com/octo/Lode/compare/feature/x?expand=1,
                        Ctrl+R: opened https://github.com/octo/Lode/compare/feature/x?expand=1
                      ],
                      otherName: [
                        Copy branch name: copied fix,
                        View on GitHub: opened https://github.com/octo/Lode/tree/main,
                        Create pull request: opened https://github.com/octo/Lode/compare/main?expand=1,
                        Ctrl+R: opened https://github.com/octo/Lode/compare/main?expand=1
                      ],
                      local: [
                        Copy branch name: copied local,
                        Ctrl+R: told local tracks main, not a branch on origin, so there is none there to create a pull request from.
                      ],
                      otherRemote: [
                        Copy branch name: copied synced,
                        Ctrl+R: told synced tracks upstream/main, not a branch on origin, so there is none there to create a pull request from.
                      ],
                      unpushed: [
                        Copy branch name: copied unpushed,
                        Create pull request…: asked unpushed isn't on origin yet,
                        Ctrl+R: asked unpushed isn't on origin yet
                      ],
                      gone: [
                        Copy branch name: copied merged,
                        Ctrl+R: told merged tracks origin/merged, which has gone from origin, deleted once it was merged say, so there is no branch there to create a pull request from.
                      ]
                    }
                    """);
        }
        finally
        {
            FolderView.SetClipboardText = setClipboardText;
            GitStatusActions.Ask = ask;
        }

        // The branch's right-click entries, each run, with what it did, then Ctrl+R, the branch
        // checked out and its state read from the repository as Lode reads it.
        async Task<List<string>> Run(string branch)
        {
            CodeCommandsTests.Scratch.Git(fork, "switch", branch);
            GitCache.Reset();
            FolderView.ReadGit = (_, cancel) => InProcessState.FromFaster(fork, cancel);
            using var form = await Show(fixture, settings, fixture.Root);
            await PaneStubs.Settled(form);
            form.OpenSite = _ => done.Add($"opened {_}");
            form.StatusBar.Tell = (_, text) => done.Add($"told {text}");
            var ran = new List<string>();
            foreach (var command in form.StatusBar.CommandsFor(form.StatusBar.Strip.Items["git"]!))
            {
                command!.Action();
                ran.Add($"{command.Text}: {done[^1]}");
            }

            var before = done.Count;
            form.Commands.RunKey(Keys.Control | Keys.R);
            ran.Add($"Ctrl+R: {string.Join(", ", done[before..])}");
            return ran;
        }
    }

    // The background fetch fetches the repositories the tabs of every window showing are in,
    // read yet or not, each once, a tab in a folder inside one counting as one in it, and the
    // repository selected in a folder of them, but not those of a window gone to the tray, nor
    // a folder in no repository.
    [Test]
    public async Task TheBackgroundFetchFetchesTheRepositoriesOfTheTabsShowing()
    {
        using var code = new CodeCommandsTests.Scratch();
        var first = code.CreateRepo("First");
        var second = code.CreateRepo("Second");
        var selected = code.CreateRepo("Selected");
        var hidden = code.CreateRepo("Hidden");
        var inside = Path.Combine(first, "src");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(code.At("Plain"));
        using var fixture = Fixture.Create();
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var form = await Show(fixture, settings, inside, second, first, code.At("Plain"), code.Root);
        using var gone = await Show(fixture, settings, hidden);
        gone.Hide();
        var listing = form.ActiveView!;
        await Pump.Until(() => listing.GitReading.IsCompleted);
        listing.List.SelectedIndices.Add(listing.Entries.ToList().FindIndex(_ => _.FullPath == selected));
        await Pump.Until(() => listing.GitRepo == selected);
        var fetched = new List<string>();
        using var fetcher = new BackgroundFetch(
            settings,
            MainForm.OpenFolders,
            new Clock(DateTimeOffset.UtcNow),
            () => [],
            _ => { },
            (repo, _) =>
            {
                lock (fetched)
                {
                    fetched.Add(repo);
                }

                return new(repo, true, "");
            });

        await fetcher.Tick();

        await Assert.That(fetched).IsEquivalentTo([first, second, selected], CollectionOrdering.Any);
    }

    // A branch tracking none isn't on origin yet, so Create pull request asks to push it first,
    // as GitHub Desktop asks to publish one. Pushed, as Push publishes it, to a branch of its name
    // on origin, the page that creates one from it opens. Declined, nothing is pushed or opened.
    [Test]
    public async Task StatusBarPushesABranchBeforeCreatingItsPullRequest()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        // On GitHub as Lode reads origin, while git goes to the scratch origin in its place.
        CodeCommandsTests.Scratch.Git(repo, "remote", "set-url", "origin", "https://github.com/octo/Repo.git");
        CodeCommandsTests.Scratch.Git(repo, "config", $"url.{code.Origin("Repo").Replace('\\', '/')}.insteadOf", "https://github.com/octo/Repo.git");
        CodeCommandsTests.Scratch.Git(repo, "switch", "-c", "feature");
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, new Settings { CodeDirectory = code.Root }, repo);
        await Pump.Until(() => form.StatusBar.PullRequestCommand() != null);
        var opened = new List<string>();
        form.OpenSite = opened.Add;
        var asked = new List<string>();
        GitStatusActions.Choice? answer = null;
        var ask = GitStatusActions.Ask;
        GitStatusActions.Ask = (_, question) =>
        {
            asked.Add($"{question.Heading}: {string.Join(", ", question.Options.Select(option => option.Text))}");
            return answer;
        };
        string declined;
        try
        {
            form.StatusBar.PullRequestCommand()!.Action();
            declined = CodeCommandsTests.Scratch.Git(repo, "ls-remote", "--heads", "origin", "feature");
            answer = GitStatusActions.Choice.Push;
            form.Commands.RunKey(Keys.Control | Keys.R);
            await Pump.Until(() => opened.Count > 0);
        }
        finally
        {
            GitStatusActions.Ask = ask;
            ActionLog.Reset();
        }

        await Assert.That(declined).IsEmpty();
        await Assert.That(asked).IsEquivalentTo(
            [
                "feature isn't on origin yet: Push, then create the pull request",
                "feature isn't on origin yet: Push, then create the pull request"
            ]);
        await Assert.That(opened).IsEquivalentTo(["https://github.com/octo/Repo/compare/feature?expand=1"]);
        await Assert.That(CodeCommandsTests.Scratch.Git(repo, "ls-remote", "--heads", "origin", "feature")).EndsWith("refs/heads/feature");
        await Assert.That(CodeCommandsTests.Scratch.Git(repo, "rev-parse", "--abbrev-ref", "feature@{upstream}")).IsEqualTo("origin/feature");
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
            Location = new(-20000, -20000),
            // WinForms keeps a window no wider than the screen unless it has a maximum size of
            // its own, and CI's screen is 1024 wide, where the actions bar would fit fewer
            // buttons. So the window is given one, then the size it opens at again, which it was
            // kept from: as wide on every machine.
            MaximumSize = new(10_000, 10_000),
            ClientSize = new(1200, 760)
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
