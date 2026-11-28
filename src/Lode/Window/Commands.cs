/// <summary>
/// A window's own commands, apart from the actions on the selection: what the ⋯ menu at the
/// right of the address bar lists, what the command palette offers and what the keys do, and
/// those of them only they run: Undo and Redo, going to the next tab or one by its number,
/// showing hidden items and system files, Terminal, Open in Claude, the coding agents, pinning
/// the folder open to Quick access, cloning a repository, purging the NuGet cache, and About.
/// </summary>
sealed class Commands
{
    const string projectUrl = "https://github.com/SimonCropp/Lode";

    // Not a URL: the issue's is built when clicked, with the version and OS in its body.
    const string raiseIssue = "raise-issue";

    // Shortcuts of the view's own commands, which its menus do not show.
    static Dictionary<string, string> viewShortcuts = new()
    {
        ["Cut"] = "Ctrl+X",
        ["Copy"] = "Ctrl+C",
        ["Paste"] = "Ctrl+V",
        ["Rename"] = "F2",
        ["Delete"] = "Delete",
        ["Properties"] = "Alt+Enter",
        ["New: Folder"] = "Ctrl+Shift+N"
    };

    MainForm window;
    Settings settings;
    Places places;

    /// <param name="window">The window whose commands they are, acting on its tab showing.</param>
    /// <param name="places">Quick access, for whether the folder open is pinned there.</param>
    public Commands(MainForm window, Settings settings, Places places)
    {
        this.window = window;
        this.settings = settings;
        this.places = places;
    }

    /// <summary>
    /// What the ⋯ menu at the right of the address bar lists: Lode's own commands. A null entry
    /// is a separator.
    /// </summary>
    public List<QuickMenu.Command?> Menu()
    {
        var commands = new List<QuickMenu.Command?>
        {
            new("New tab", window.NewTab)
            {
                Glyph = Icons.NewTab,
                Shortcut = "Ctrl+T"
            },
            new("New window", window.NewWindow)
            {
                Glyph = Icons.NewWindow,
                Shortcut = "Ctrl+N"
            },
            new("Reopen closed tab", window.ReopenClosedTab, window.CanReopenClosedTab)
            {
                Glyph = Icons.Reopen,
                Shortcut = "Ctrl+Shift+T"
            },
            QuickMenu.Submenu("Recently closed", window.RecentlyClosed()) with
            {
                Enabled = window.CanReopenClosedTab,
                Glyph = Icons.History
            },
            new("Command palette", ShowPalette)
            {
                Glyph = Icons.CommandPalette,
                Shortcut = "Ctrl+Shift+P"
            },
            new("Find in files", window.FindHost.Open)
            {
                Glyph = Icons.Search,
                Shortcut = "Ctrl+Shift+F"
            },
            new("Clone a repository…", ShowClone)
            {
                Glyph = Icons.Clone,
                Shortcut = "Ctrl+Shift+O"
            },
            new("Open NuGet cache", OpenNugetCache)
            {
                Glyph = Icons.OpenInNewTab
            },
            new("Purge NuGet cache", PurgeNugetCache)
            {
                Glyph = Icons.Purge
            },
            null,
            // The list's views, as Explorer's View menu has them, above what it shows.
            new("Large icons", () => ShowLargeIcons(true), window.ActiveView != null)
            {
                Glyph = Icons.LargeIcons,
                Shortcut = "Ctrl+Shift+2",
                Checked = window.ActiveView is {LargeIcons: true}
            },
            new("Details", () => ShowLargeIcons(false), window.ActiveView != null)
            {
                Glyph = Icons.Details,
                Shortcut = "Ctrl+Shift+6",
                Checked = window.ActiveView is {LargeIcons: false}
            },
            new("Show hidden items", ToggleHidden)
            {
                Glyph = Icons.ShowHidden,
                Shortcut = "Ctrl+H",
                Checked = settings.ShowHidden
            },
            new("Show system files", ToggleSystem)
            {
                Glyph = Icons.ShowSystem,
                Checked = settings.ShowSystem
            }
        };
        var panes = window.GitPanes;
        if (panes.Repo is { } repo)
        {
            commands.AddRange(
            [
                null,
                new("Changes", () => panes.Toggle(GitPane.Changes))
                {
                    Glyph = Icons.Changes,
                    Shortcut = "Ctrl+Shift+G"
                },
                new("History", () => panes.Toggle(GitPane.History))
                {
                    Glyph = Icons.History,
                    Shortcut = "Ctrl+Shift+H"
                },
                new("Compare to branch…", () => CodeMenu.CompareToBranch(window, repo))
                {
                    Glyph = Icons.History
                }
            ]);
        }

        // For the repository the status bar is about, which is the panes' but for one selected in
        // a folder of repositories, so with them where they are.
        if (window.StatusBar.PullRequestCommand() is { } pullRequest)
        {
            if (panes.Repo == null)
            {
                commands.Add(null);
            }

            commands.Add(pullRequest);
        }

        if (PinnablePath() is { } path)
        {
            var pinned = IsPinned(path);
            commands.Add(
                new(PinMenuText(path), TogglePinned)
                {
                    Glyph = pinned ? Icons.Unpin : Icons.Pin
                });
        }

        commands.AddRange(
        [
            null,
            new("Options…", window.ShowOptions)
            {
                Glyph = Icons.Options
            },
            new("About Lode", ShowAbout)
            {
                Glyph = Icons.About
            },
            null,
            new("Exit Lode", MainForm.RequestExit)
            {
                Glyph = Icons.Exit
            }
        ]);
        return commands;
    }

    /// <summary>
    /// What the command palette offers: the window's commands, then those the right-click menu
    /// would show for the selection, or for the folder when nothing is selected. Commands that
    /// cannot run now are left out.
    /// </summary>
    public List<CommandPalette.Command> Palette()
    {
        var view = window.ActiveView;
        var tabs = window.Tabs;
        var page = tabs.SelectedTab;
        var index = tabs.SelectedIndex;
        var panes = window.GitPanes;
        var commands = new List<CommandPalette.Command>();

        void Add(string name, Action action, string shortcut = "", bool enabled = true)
        {
            if (enabled)
            {
                commands.Add(new(name, action, shortcut));
            }
        }

        Add("Back", () => window.ActiveView?.GoBack(), "Alt+Left", view?.CanGoBack == true);
        Add("Forward", () => window.ActiveView?.GoForward(), "Alt+Right", view?.CanGoForward == true);
        Add("Up", () => window.ActiveView?.GoUp(), "Alt+Up", view?.CanGoUp == true);
        Add("Refresh", window.Reload, "F5");
        Add("Go to address", window.AddressBar.FocusAddress, "Ctrl+L");
        Add("Search this folder", window.AddressBar.FocusFilter, "Ctrl+F");
        Add("New tab", window.NewTab, "Ctrl+T");
        Add("New window", window.NewWindow, "Ctrl+N");
        Add("Find in files", window.FindHost.Open, "Ctrl+Shift+F");
        Add("Clone a repository…", ShowClone, "Ctrl+Shift+O");
        Add("Open NuGet cache", OpenNugetCache);
        Add("Purge NuGet cache", PurgeNugetCache);
        if (panes.Repo is { } repo)
        {
            Add("Changes", () => panes.Toggle(GitPane.Changes), "Ctrl+Shift+G");
            Add("History", () => panes.Toggle(GitPane.History), "Ctrl+Shift+H");
            Add("Compare to branch…", () => CodeMenu.CompareToBranch(window, repo));
        }

        if (window.StatusBar.PullRequestCommand() is { } pullRequest)
        {
            Add(pullRequest.Text, pullRequest.Action, "Ctrl+R");
        }

        if (page != null)
        {
            if (view != null)
            {
                Add("Duplicate tab", () => window.AddTab(view.CurrentPath, true, intoZip: true));
            }

            Add("Close tab", () => window.CloseTab(page), "Ctrl+W");
            Add("Close other tabs", () => window.CloseOtherTabs(page), enabled: tabs.TabCount > 1);
            Add("Close tabs to the left", () => window.CloseTabsToTheLeft(page), enabled: index > 0);
            Add("Close tabs to the right", () => window.CloseTabsToTheRight(page), enabled: index < tabs.TabCount - 1);
            Add("Close all tabs", window.CloseAllTabs);
        }

        Add("Reopen closed tab", window.ReopenClosedTab, "Ctrl+Shift+T", window.CanReopenClosedTab);
        var history = UndoHistory.Instance;
        Add(history.UndoText, Undo, "Ctrl+Z", history.CanUndo);
        Add(history.RedoText, Redo, "Ctrl+Y", history.CanRedo);
        Add("Next tab", () => SelectAdjacentTab(1), "Ctrl+Tab", tabs.TabCount > 1);
        Add("Previous tab", () => SelectAdjacentTab(-1), "Ctrl+Shift+Tab", tabs.TabCount > 1);
        Add(settings.ShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden, "Ctrl+H");
        Add(settings.ShowSystem ? "Hide system files" : "Show system files", ToggleSystem);
        Add("Large icons", () => ShowLargeIcons(true), "Ctrl+Shift+2", view != null);
        Add("Details", () => ShowLargeIcons(false), "Ctrl+Shift+6", view != null);
        Add("Terminal", () => OpenTerminal(admin: false), enabled: PinnablePath() != null);
        Add("Terminal (admin)", () => OpenTerminal(admin: true), enabled: PinnablePath() != null);
        if (ClaudeDesktop.IsInstalled())
        {
            Add("Open in Claude", OpenInClaude, enabled: PinnablePath() != null);
        }

        // Named as the folder's own would be, which these stand in for when a file is selected.
        foreach (var agent in CodingAgents.Installed())
        {
            Add($"Coding agent: {agent.Name}", () => OpenAgent(agent), enabled: PinnablePath() != null);
        }

        if (PinMenuText() is { } pinText)
        {
            Add(pinText, TogglePinned);
        }

        if (view != null)
        {
            var selection = view.SelectedEntries();
            List<QuickMenu.Command?> viewCommands;
            if (selection.Count > 0)
            {
                // Sort by, which the right-click menu has only where nothing is clicked, as the
                // list is sorted whatever is selected.
                viewCommands =
                [
                    .. view.ItemCommands(selection),
                    LodeActions.Submenu("sortBy", view.SortCommands())
                ];
            }
            else if (!view.ListsPlaces)
            {
                viewCommands = view.BackgroundCommands(FileOperations.ReadClipboard() != null);
            }
            else
            {
                viewCommands = [];
            }

            var selectionCommands = new List<CommandPalette.Command>();
            AddViewCommands(selectionCommands, view, viewCommands, "");

            // The window's Terminal, Open in Claude and Coding agent open in the tab's folder.
            // Where the selection has its own, that one stands in, in the window's place, so it
            // opens in the folder selected, as the right-click menu and the actions bar do.
            for (var position = 0; position < commands.Count; position++)
            {
                if (OpensInTheFolder(commands[position].Name) &&
                    selectionCommands.FirstOrDefault(_ => SameName(_, commands[position])) is { } own)
                {
                    commands[position] = own;
                }
            }

            commands.AddRange(selectionCommands);
        }

        Add("Options…", window.ShowOptions);
        Add("About Lode", ShowAbout);
        Add("Exit Lode", MainForm.RequestExit);

        // A view command named as a window one (New folder, say) would only repeat it.
        return commands
            .DistinctBy(_ => _.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static bool OpensInTheFolder(string name) =>
        name is "Terminal" or "Terminal (admin)" or "Open in Claude" ||
        name.StartsWith("Coding agent: ", StringComparison.Ordinal);

    static bool SameName(CommandPalette.Command command, CommandPalette.Command other) =>
        string.Equals(command.Name, other.Name, StringComparison.CurrentCultureIgnoreCase);

    // A submenu's entries are named after it: "New: Text Document", "Open with: Notepad".
    static void AddViewCommands(List<CommandPalette.Command> commands, FolderView view, IReadOnlyList<QuickMenu.Command?> viewCommands, string prefix)
    {
        foreach (var command in viewCommands)
        {
            if (command is not { Enabled: true })
            {
                continue;
            }

            var name = prefix + command.Text;
            if (command.Children != null)
            {
                AddViewCommands(commands, view, command.Children, $"{name}: ");
                continue;
            }

            var action = command.Action;
            commands.Add(
                new(
                    name,
                    () =>
                    {
                        // Back on the list first, so Rename has an item to edit.
                        view.FocusList();
                        action();
                    },
                    viewShortcuts.GetValueOrDefault(name, "")));
        }
    }

    /// <summary>
    /// Runs what <paramref name="keyData"/> does, pressed anywhere in the window. False for a key
    /// the window does nothing with, which goes on to the control with the focus.
    /// </summary>
    public bool RunKey(Keys keyData)
    {
        if (TabNumber(keyData) is > 0 and var number)
        {
            SelectTabAt(number);
            return true;
        }

        switch (keyData)
        {
            case Keys.Alt | Keys.Left:
                window.ActiveView?.GoBack();
                return true;
            case Keys.Alt | Keys.Right:
                window.ActiveView?.GoForward();
                return true;
            case Keys.Alt | Keys.Up:
                window.ActiveView?.GoUp();
                return true;
            case Keys.F5:
                window.Reload();
                return true;
            case Keys.Control | Keys.T:
                window.NewTab();
                return true;
            case Keys.Control | Keys.W:
                if (window.Tabs.SelectedTab is { } selected)
                {
                    window.CloseTab(selected);
                }

                return true;
            case Keys.Control | Keys.Z when !IsEditingText():
                Undo();
                return true;
            case Keys.Control | Keys.Y when !IsEditingText():
            case Keys.Control | Keys.Shift | Keys.Z when !IsEditingText():
                Redo();
                return true;
            case Keys.Control | Keys.Shift | Keys.F:
                window.FindHost.Open();
                return true;
            case Keys.Control | Keys.Shift | Keys.T:
                window.ReopenClosedTab();
                return true;
            case Keys.Control | Keys.Shift | Keys.P:
                ShowPalette();
                return true;
            case Keys.Control | Keys.Tab:
                SelectAdjacentTab(1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                SelectAdjacentTab(-1);
                return true;
            // With Find Results showing, which has no address, the next match and the one before.
            case Keys.F4 when window.FindHost.ResultsShowing:
                window.FindHost.ResultsView?.GoToMatch(true);
                return true;
            case Keys.Shift | Keys.F4 when window.FindHost.ResultsShowing:
                window.FindHost.ResultsView?.GoToMatch(false);
                return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F4:
                window.AddressBar.FocusAddress();
                return true;
            case Keys.Control | Keys.F:
            case Keys.F3:
                window.AddressBar.FocusFilter();
                return true;
            case Keys.Control | Keys.N:
                window.NewWindow();
                return true;
            case Keys.Control | Keys.H:
                ToggleHidden();
                return true;
            // Explorer's keys for its views, those Lode has.
            case Keys.Control | Keys.Shift | Keys.D2:
                ShowLargeIcons(true);
                return true;
            case Keys.Control | Keys.Shift | Keys.D6:
                ShowLargeIcons(false);
                return true;
            case Keys.Control | Keys.B:
                window.StatusBar.ShowBranches();
                return true;
            case Keys.Control | Keys.R:
                window.StatusBar.CreatePullRequest();
                return true;
            case Keys.Control | Keys.Shift | Keys.G:
                window.GitPanes.Toggle(GitPane.Changes);
                return true;
            case Keys.Control | Keys.Shift | Keys.H:
                window.GitPanes.Toggle(GitPane.History);
                return true;
            case Keys.Control | Keys.Shift | Keys.O:
                ShowClone();
                return true;
        }

        return false;
    }

    // The number Ctrl was held with, on the top row or the number pad, or 0.
    static int TabNumber(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != Keys.Control)
        {
            return 0;
        }

        var key = keyData & Keys.KeyCode;
        if (key is >= Keys.D1 and <= Keys.D9)
        {
            return key - Keys.D0;
        }

        if (key is >= Keys.NumPad1 and <= Keys.NumPad9)
        {
            return key - Keys.NumPad0;
        }

        return 0;
    }

    // Ctrl+1 to Ctrl+8 go to the tab at that place, and Ctrl+9 to the last, as in Explorer and
    // the browsers. One that isn't there does nothing.
    void SelectTabAt(int number)
    {
        var tabs = window.Tabs;
        var index = number - 1;
        if (number == 9)
        {
            index = tabs.TabCount - 1;
        }

        if (index < 0 ||
            index >= tabs.TabCount)
        {
            return;
        }

        tabs.SelectedIndex = index;
        window.ActiveView?.FocusList();
    }

    void SelectAdjacentTab(int offset)
    {
        var tabs = window.Tabs;
        if (tabs.TabCount > 1)
        {
            tabs.SelectedIndex = (tabs.SelectedIndex + offset + tabs.TabCount) % tabs.TabCount;
            window.ActiveView?.FocusList();
        }
    }

    // Typing in a box (the address, the filter, a name being edited) keeps Ctrl+Z for the text.
    static unsafe bool IsEditingText()
    {
        var focus = GetFocus();
        if (focus == IntPtr.Zero)
        {
            return false;
        }

        const int capacity = 64;
        var name = stackalloc char[capacity];
        var length = GetClassNameW(focus, name, capacity);
        return new CharSpan(name, Math.Max(length, 0)).Contains("Edit", StringComparison.OrdinalIgnoreCase);
    }

    // Ctrl+Z and Ctrl+Y: through the tab's view, which says when one can't be done; with Find
    // Results showing, which has none, the window says.
    void Undo()
    {
        if (window.ActiveView is { } view)
        {
            view.Undo();
            return;
        }

        RunUndo(UndoHistory.Instance.Undo);
    }

    void Redo()
    {
        if (window.ActiveView is { } view)
        {
            view.Redo();
            return;
        }

        RunUndo(UndoHistory.Instance.Redo);
    }

    async void RunUndo(Func<IntPtr, Task<string?>> step)
    {
        var failure = await step(window.Handle);
        if (failure != null &&
            !window.IsDisposed)
        {
            MessageBoxes.Show(window, failure, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Ctrl+Shift+P: the command palette.
    /// </summary>
    public void ShowPalette() =>
        CommandPalette.Show(window, Palette());

    /// <summary>
    /// Ctrl+H: hidden items shown, or hidden again, in every window.
    /// </summary>
    public void ToggleHidden()
    {
        settings.ShowHidden = !settings.ShowHidden;
        MainForm.ApplySettingsToAllWindows(SettingsChange.Listing);
    }

    void ToggleSystem()
    {
        settings.ShowSystem = !settings.ShowSystem;
        MainForm.ApplySettingsToAllWindows(SettingsChange.Listing);
    }

    // The tab showing's list in Large icons or in Details, its folder from now on.
    void ShowLargeIcons(bool large) =>
        window.ActiveView?.ShowLargeIcons(large);

    void OpenTerminal(bool admin)
    {
        if (window.ActiveView is {IsReadOnly: false} view)
        {
            FileOperations.OpenTerminal(view.CurrentPath, admin);
        }
    }

    void OpenInClaude()
    {
        if (PinnablePath() is { } path)
        {
            ClaudeDesktop.Open(window, path);
        }
    }

    void OpenAgent(CodingAgents.Agent agent)
    {
        if (PinnablePath() is { } path)
        {
            CodingAgents.Open(agent, [path]);
        }
    }

    string? PinnablePath()
    {
        if (window.ActiveView is not { } view ||
            Locations.IsThisPC(view.CurrentPath) ||
            view.InArchive)
        {
            return null;
        }

        return view.CurrentPath;
    }

    /// <summary>
    /// What the main menu's pin item says for the active folder, or null when it is hidden.
    /// </summary>
    public string? PinMenuText()
    {
        if (PinnablePath() is not { } path)
        {
            return null;
        }

        return PinMenuText(path);
    }

    // "This folder": the one the tab shows, whatever is selected. Pin to Quick access, the
    // action, is for the selection.
    string PinMenuText(string path)
    {
        if (IsPinned(path))
        {
            return "Unpin this folder from Quick access";
        }

        return "Pin this folder to Quick access";
    }

    /// <summary>
    /// Whether <paramref name="path"/> is pinned to Quick access.
    /// </summary>
    public bool IsPinned(string path) =>
        places.Pinned.Any(_ => Locations.Same(_, path));

    void TogglePinned()
    {
        if (PinnablePath() is { } path)
        {
            ShellContextMenu.SetPinned(window.Handle, path, !IsPinned(path));
        }
    }

    /// <summary>
    /// Asks what to clone, and where to, then clones it in the Actions panel, opening a tab on
    /// the clone once it is there. Tests answer for the user.
    /// </summary>
    internal Func<CloneDialog, DialogResult> ShowCloneDialog = dialog => dialog.ShowDialog(dialog.Owner);

    /// <summary>
    /// Ctrl+Shift+O: clones a repository, asking what and where to first.
    /// </summary>
    public void ShowClone()
    {
        // Into the code folder, or, without one, the folder open.
        var parent = settings.CodeDirectory;
        if (parent == null ||
            !Directory.Exists(parent))
        {
            parent = PinnablePath() ?? Locations.Home;
        }

        using var dialog = new CloneDialog(settings, parent)
        {
            Owner = window
        };
        if (ShowCloneDialog(dialog) != DialogResult.OK)
        {
            return;
        }

        Clone(dialog.Remote, dialog.Destination);
    }

    // Its submodules too, without asking, as the repository was picked or typed here, not handed
    // over by a link from a web page, which RepositoryClone.Offer asks about. One that can't be
    // fetched fails the entry, saying so, but the clone stays, and opens all the same. So does
    // one cancelled once it is cloned, as a fork's upstream is set up or the submodules come:
    // the clone is there, and cloning it again would only say so. What came after it is left
    // undone, upstream for the next fetch or Sync fork, the submodules for git submodule
    // update, and the entry says Cancelled, with what was done.
    async void Clone(string remote, string destination)
    {
        var cloned = false;
        await ActionLog.Run(
            "Git clone",
            [destination],
            async (progress, cancel) =>
            {
                var result = await Task.Run(() => RepositoryClone.Clone(remote, destination, null, progress.Report, cancel), cancel);
                if (result.Succeeded)
                {
                    cloned = true;

                    // Cancel.None, not the token: the submodules watch the token themselves, and
                    // given it, a Cancel as upstream was set up would have the await throw instead,
                    // and the entry say nothing of the clone.
                    result = await Task.Run(() => RepositoryClone.WithSubmodules(result, progress.Report, cancel), Cancel.None);
                }

                return CodeCommands.GitOutcome([result]);
            });
        if (cloned &&
            !window.IsDisposed)
        {
            window.AddTab(destination, true);
        }
    }

    // Where restore puts the packages it fetches, in a tab of its own. Purged, or never restored
    // to, it isn't there, and says so.
    void OpenNugetCache()
    {
        var folder = NuGetCache.PackagesFolder();
        if (!Directory.Exists(folder))
        {
            MessageBoxes.Show(window, $"The NuGet cache isn't there:\n{folder}", "Open NuGet cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        window.AddTab(folder, true);
    }

    // In the Actions panel, as Update NuGets is: deleting every package restored takes a while,
    // and fails where one is open, in a build or Visual Studio say.
    static void PurgeNugetCache() =>
        _ = ActionLog.Run("Purge NuGet cache", [NuGetCache.PackagesFolder()], NuGetCache.Purge);

    void ShowAbout()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        var page = new TaskDialogPage
        {
            Caption = "About Lode",
            Heading = $"Lode {version}",
            Text = $"""
                    A tabbed file manager with the classic Windows context menu.

                    <a href="{projectUrl}">{projectUrl}</a>

                    <a href="{raiseIssue}">Raise an issue</a>
                    """,
            EnableLinks = true,
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK }
        };
        page.LinkClicked += (_, args) =>
        {
            if (args.LinkHref == raiseIssue)
            {
                IssueLauncher.Launch();
                return;
            }

            IssueLauncher.OpenUrl(args.LinkHref);
        };
        MessageBoxes.ShowDialog(window, page);
    }
}
