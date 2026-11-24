/// <summary>
/// A window's own commands, apart from the actions on the selection: what the ⋯ menu at the
/// right of the address bar lists and what the command palette offers, and those of them only
/// they and the keys run: showing hidden items and system files, Terminal, Open in Claude, the
/// coding agents, pinning the folder open to Quick access, cloning a repository, and About.
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
            null,
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
        if (panes.Repo != null)
        {
            var changesPinned = panes.IsPinned(GitPane.Changes);
            var historyPinned = panes.IsPinned(GitPane.History);
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
                new("Pin changes", () => panes.SetPinned(GitPane.Changes, !changesPinned))
                {
                    Checked = changesPinned
                },
                new("Pin history", () => panes.SetPinned(GitPane.History, !historyPinned))
                {
                    Checked = historyPinned
                }
            ]);
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
        if (panes.Repo != null)
        {
            Add("Changes", () => panes.Toggle(GitPane.Changes), "Ctrl+Shift+G");
            Add("History", () => panes.Toggle(GitPane.History), "Ctrl+Shift+H");
            Add(panes.IsPinned(GitPane.Changes) ? "Unpin changes" : "Pin changes", () => panes.SetPinned(GitPane.Changes, !panes.IsPinned(GitPane.Changes)));
            Add(panes.IsPinned(GitPane.History) ? "Unpin history" : "Pin history", () => panes.SetPinned(GitPane.History, !panes.IsPinned(GitPane.History)));
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
        Add(history.UndoText, window.Undo, "Ctrl+Z", history.CanUndo);
        Add(history.RedoText, window.Redo, "Ctrl+Y", history.CanRedo);
        Add("Next tab", () => window.SelectAdjacentTab(1), "Ctrl+Tab", tabs.TabCount > 1);
        Add("Previous tab", () => window.SelectAdjacentTab(-1), "Ctrl+Shift+Tab", tabs.TabCount > 1);
        Add(settings.ShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden, "Ctrl+H");
        Add(settings.ShowSystem ? "Hide system files" : "Show system files", ToggleSystem);
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
                viewCommands = view.ItemCommands(selection);
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

    async void Clone(string remote, string destination)
    {
        var entry = await ActionLog.Run(
            "Git clone",
            [destination],
            async (progress, cancel) =>
            {
                var result = await Task.Run(() => RepositoryClone.Clone(remote, destination, null, progress.Report, cancel), cancel);
                return CodeCommands.GitOutcome([result]);
            });
        if (entry.State == ActionLog.State.Succeeded &&
            !window.IsDisposed)
        {
            window.AddTab(destination, true);
        }
    }

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
        TaskDialog.ShowDialog(window, page);
    }
}
