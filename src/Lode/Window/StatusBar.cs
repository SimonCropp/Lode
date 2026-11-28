/// <summary>
/// A window's status bar, along its bottom: what the tab showing has, or has selected, or what
/// Find Results has found, then a newer Lode when there is one, the state of the repository the
/// folder is in, the site its origin is on, and the free space. Each part acts on what it says.
/// </summary>
sealed class StatusBar : IDisposable
{
    Form window;
    Settings settings;
    Action<string> openTab;
    Action<string> openSite;

    // The instance the bar listens to, so it stops listening to that one when the window closes.
    Updates updates;

    // The bar's tooltips, shown above what they are for. The bar's own are shown below the
    // pointer, and at the bottom of the screen Windows moves them up over it, which the bar
    // takes for the pointer leaving: the tooltip hides, the pointer is over the bar again, and
    // it shows again, over and over.
    ToolTip tip = new()
    {
        ShowAlways = true
    };

    ToolStripStatusLabel textLabel = new()
    {
        TextAlign = ContentAlignment.MiddleLeft
    };

    // Right after what the search box found, while it has found anything or not: the same text
    // searched for inside the files instead, with Find in Files. Then the room left, so the
    // link sits beside the count rather than across the bar from it.
    ToolStripStatusLabel searchInsideLabel = new()
    {
        Name = "searchInside",
        Text = "Search inside the files",
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Margin = new(12, 3, 0, 2),
        Visible = false
    };

    ToolStripStatusLabel room = new()
    {
        Spring = true
    };

    // Before the free space: the state of the repository the folder is in, a part for each of
    // what GitState says, each acting on what it says. The branch opens the branches above it,
    // to switch to, as GitHub Desktop's Current branch has them.
    ChevronDropDownButton gitButton = new("", Icons.ChevronUp)
    {
        Name = "git",
        Margin = new(0, 1, 0, 0),
        ToolTipText = "Switch branch (Ctrl+B)",
        Visible = false
    };

    // After the branch, after a dot: the most recent tag the commit checked out has in its
    // history, as git describe --tags finds it, with a tag before it as the branch has a branch.
    // Not one of GitState's parts, which the Git column says too, beside a Tag column of its own.
    ToolStripStatusLabel tagDot = Dot();
    ToolStripStatusLabel tagLabel = new()
    {
        Name = "gitTag",
        Margin = new(0, 1, 0, 0),
        ToolTipText = "The most recent tag: the commit checked out's, or the nearest before it",
        Visible = false
    };

    // After the branch and its tag, each after a dot: that there are no commits yet, which there
    // is nothing to do about, then the changes, to commit or discard, and the commits to push
    // and to pull.
    Dictionary<GitState.PartKind, (ToolStripStatusLabel Dot, ToolStripItem Item)> gitParts = [];

    // The repository the branch and the parts after it are about, and its state as they say it:
    // what clicking them acts on. Kept while the Diff tab shows, which leaves the bar as the
    // folder's tab had it, and has no folder of its own to ask.
    string? gitRepo;
    GitState? gitState;

    // After the branch: the site the repository's origin is on, GitHub say, which it opens.
    ToolStripStatusLabel siteLabel = new()
    {
        Name = "site",
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Margin = new(0, 3, 12, 2),
        Visible = false
    };

    ToolStripStatusLabel spaceLabel = new();

    // Beside the free space: a newer Lode, when there is one. Clicking it opens Options at Updates.
    ToolStripStatusLabel updateLabel = new()
    {
        Name = "update",
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        ToolTipText = "Open Options to update",
        Margin = new(0, 3, 12, 2),
        Visible = false
    };

    // The mouse button last pressed on the branch: right, its click shows the branch's menu, not
    // the branches.
    MouseButtons branchPressed;

    // When the branches last closed, as Environment.TickCount64. Clicking the branch while they
    // are open closes them, as the click goes to the window first, and then the click itself
    // would open them again.
    long branchesClosedAt = long.MinValue / 2;

    // The scale the bar's sizes are for, as a DPI: its window's as it is made, then the
    // monitor's it is on.
    int dpi;

    /// <param name="window">The window the bar is along the bottom of, whose font and scale it has.</param>
    /// <param name="updates">Whether there is a newer Lode, which the bar follows.</param>
    /// <param name="activeView">The tab showing's view, null while there is none.</param>
    /// <param name="showOptions">Opens Options at the page named.</param>
    /// <param name="openSite">Opens a repository's page on its site, in the browser.</param>
    /// <param name="openTab">Opens a folder in a tab, for a branch's commands.</param>
    public StatusBar(Form window, Settings settings, Updates updates, Func<FolderView?> activeView, Action<string> showOptions, Action<string> openSite, Action<string> openTab)
    {
        this.window = window;
        this.settings = settings;
        this.updates = updates;
        this.openTab = openTab;
        this.openSite = openSite;
        Tell = (caption, text) => MessageBoxes.Show(window, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

        // White, as Explorer's is, with its text further in from the edge, and no grip: Windows
        // 11 windows are resized from their edges.
        // In the window's font, as the folder tree and the list are, rather than the smaller one
        // a tool strip has otherwise.
        Strip = new()
        {
            Renderer = new ExplorerRenderer(),
            SizingGrip = false,
            Font = window.Font
        };
        Strip.Items.Add(textLabel);
        Strip.Items.Add(searchInsideLabel);
        Strip.Items.Add(room);
        Strip.Items.Add(updateLabel);
        Strip.Items.Add(gitButton);
        Strip.Items.Add(tagDot);
        Strip.Items.Add(tagLabel);
        AddGitPart(
            GitState.PartKind.NoCommits,
            new ToolStripStatusLabel
            {
                Margin = new(0, 3, 0, 2)
            });
        AddGitPart(GitState.PartKind.Changes, GitPartButton("Commit or discard them", GitState.PartKind.Changes));
        AddGitPart(GitState.PartKind.Ahead, GitPartButton("Push them", GitState.PartKind.Ahead));
        AddGitPart(GitState.PartKind.Behind, GitPartButton("Pull them", GitState.PartKind.Behind));
        AddGitPart(GitState.PartKind.ForkBehind, GitPartButton("Sync the fork with upstream", GitState.PartKind.ForkBehind));
        Strip.Items.Add(siteLabel);
        Strip.Items.Add(spaceLabel);

        // A branch before the name, as GitHub Desktop has, and a tag before the tag.
        gitButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        gitButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        tagLabel.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        tagLabel.TextImageRelation = TextImageRelation.ImageBeforeText;
        dpi = window.DeviceDpi;
        ShowAtScale();
        // The bar's icons are drawn at a size, and its spacing is in pixels, neither of which the
        // strip scales itself. So on a monitor at another scale than the window was made at, the
        // window opening there or moved there, they are made again for it, as the text beside
        // them is scaled for it.
        Strip.HandleCreated += (_, _) => ScaleTo(Strip.DeviceDpi);
        Strip.DpiChangedAfterParent += (_, _) => ScaleTo(Strip.DeviceDpi);
        gitButton.MouseDown += (_, args) => branchPressed = args.Button;
        gitButton.Click += (_, _) =>
        {
            var pressed = branchPressed;
            branchPressed = MouseButtons.None;
            if (pressed != MouseButtons.Right)
            {
                ShowBranches();
            }
        };
        gitButton.MouseUp += (_, args) => ShowItemMenu(gitButton, args);
        tagLabel.MouseUp += (_, args) => ShowItemMenu(tagLabel, args);
        updateLabel.Click += (_, _) => showOptions("Updates");
        searchInsideLabel.Click += (_, _) => activeView()?.SearchInsideTheFiles();
        siteLabel.Click += (_, _) =>
        {
            if (siteLabel.Tag is RemoteSite site)
            {
                openSite(site.Url);
            }
        };
        foreach (ToolStripItem item in Strip.Items)
        {
            item.MouseHover += (_, _) => ShowTip(item);
            item.MouseLeave += (_, _) => tip.Hide(Strip);
            item.MouseDown += (_, _) => tip.Hide(Strip);
        }

        updates.Changed += OnUpdatesChanged;
        ShowUpdate();
    }

    /// <summary>
    /// The strip the bar is, for the window to dock along its bottom.
    /// </summary>
    public StatusStrip Strip { get; }

    /// <summary>
    /// Shows the bar at the scale of <paramref name="dpi"/>, the monitor's it is on. Tests call
    /// it, as they run unaware of scale.
    /// </summary>
    internal void ScaleTo(int dpi)
    {
        if (this.dpi == dpi)
        {
            return;
        }

        this.dpi = dpi;
        ShowAtScale();
        // The room after the branch and after the last part, which they are given as they show.
        ShowGitParts(gitRepo, gitState);
    }

    // A size at 100%, in pixels at the bar's scale, as LogicalToDeviceUnits gives it.
    int Scaled(int logical) =>
        (int)Math.Round(dpi / 96.0 * logical);

    // The bar's spacing, and the branch's and the tag's icons, at its scale. The branch's
    // padding is the room for its chevron, which is on the bar by now, so is scaled as the bar
    // is.
    void ShowAtScale()
    {
        Strip.Padding = new(Scaled(10), Scaled(2), Scaled(10), Scaled(2));
        gitButton.Padding = new(0, 0, gitButton.ChevronWidth, 0);
        gitButton.Image = Icons.Bitmap(Icons.Branch, Scaled(16), SystemColors.ControlText, Scaled(4));
        tagLabel.Image = Icons.Bitmap(Icons.Tag, Scaled(16), SystemColors.ControlText, Scaled(4));
    }

    /// <summary>
    /// Shows what the tab showing has: what is selected in it, or what it holds, the search
    /// inside the files while its search box has found anything, and its repository, site and
    /// free space. Only ever the active view, with its selection as the actions bar has it.
    /// </summary>
    public void Update(FolderView view, IReadOnlyList<FileEntry> selection)
    {
        textLabel.Text = view.StatusFor(selection);
        var searchesInside = view.CanSearchInsideTheFiles;
        if (searchInsideLabel.Visible != searchesInside)
        {
            searchInsideLabel.Visible = searchesInside;
        }

        if (searchesInside)
        {
            searchInsideLabel.ToolTipText = $"Search for \"{view.Filter}\" in what the files hold, with Find in Files (Ctrl+Enter)";
        }

        ShowGitParts(view.GitRepo, view.GitRepoState);
        var site = view.GitSite;
        siteLabel.Tag = site;
        siteLabel.Visible = site != null;
        if (site != null)
        {
            siteLabel.Text = site.Name;
            siteLabel.ToolTipText = $"Open {site.Url}";
        }

        spaceLabel.Text = view.FreeSpaceText;
    }

    /// <summary>
    /// As Find Results shows: what it has found so far, <paramref name="progress"/>, and none of
    /// what is a folder's, its free space, its repository, or searching inside its files.
    /// </summary>
    public void ShowResults(string progress)
    {
        ShowProgress(progress);
        searchInsideLabel.Visible = false;
        spaceLabel.Text = "";
        siteLabel.Visible = false;
        ShowGitParts(null, null);
    }

    /// <summary>
    /// What Find Results has found so far, ten times a second while searching. Set once, and
    /// only when it says something else: the status bar emptied, even for a moment, is shorter,
    /// and everything above it moves.
    /// </summary>
    public void ShowProgress(string text)
    {
        if (textLabel.Text != text)
        {
            textLabel.Text = text;
        }
    }

    // Its text as of now: the site's changes with the folder.
    void ShowTip(ToolStripItem item)
    {
        var text = item.ToolTipText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Windows draws tooltips in the status font, with a margin round the text.
        var font = SystemFonts.StatusFont ?? window.Font;
        var height = TextRenderer.MeasureText(text, font).Height + Scaled(12);
        tip.Show(text, Strip, TipLocation(item.Bounds, height, Scaled(4)), tip.AutoPopDelay);
    }

    /// <summary>
    /// Where a tooltip <paramref name="height"/> high goes for a status bar item at
    /// <paramref name="item"/>, in the bar's coordinates: over the item's left, wholly above the
    /// bar with a <paramref name="gap"/>, so never under the pointer, which is on the bar.
    /// </summary>
    internal static Point TipLocation(Rectangle item, int height, int gap) =>
        new(item.Left, -height - gap);

    /// <summary>
    /// The right-click entries of the branch or the tag on the bar: to copy its name, as GitHub
    /// Desktop's branch menu has Copy branch name, and to view it on the site origin is on, as
    /// History views a commit. Detached, the commit, in the branch's place. A branch is viewed as
    /// origin has it, by its name there, so only one tracking a branch on origin that is there
    /// still: one tracking none isn't on the site yet, and one whose branch on origin has gone,
    /// one tracking a branch of the repository's own, or another remote's, upstream's in a fork
    /// say, isn't there by its name, so none of them is viewed there. A branch has Create pull
    /// request too, where it is viewed there, and, asking to push it first, where it tracks none:
    /// see <see cref="PullRequest"/>. None for anything else, or with no repository shown.
    /// </summary>
    internal List<QuickMenu.Command?> CommandsFor(ToolStripItem item)
    {
        if (gitState is not { } state)
        {
            return [];
        }

        var site = siteLabel.Tag as RemoteSite;
        if (item == tagLabel &&
            state.Tag is { } tag)
        {
            return WithView(Copy("Copy tag name", tag), site?.TagUrl(tag));
        }

        if (item != gitButton)
        {
            return [];
        }

        if (state.Branch is { } branch)
        {
            string? url = null;
            if (state.OriginBranch is { } onOrigin)
            {
                url = site?.BranchUrl(onOrigin);
            }

            var commands = WithView(Copy("Copy branch name", branch), url);
            if (PullRequestCommand() is { } pullRequest)
            {
                commands.Add(pullRequest);
            }

            return commands;
        }

        if (state.Commit is { } sha)
        {
            return WithView(Copy("Copy SHA", sha), site?.CommitUrl(sha));
        }

        return [];

        static QuickMenu.Command Copy(string text, string value) =>
            new(text, () => FolderView.SetClipboardText(value))
            {
                Glyph = Icons.Copy
            };

        List<QuickMenu.Command?> WithView(QuickMenu.Command copy, string? url)
        {
            if (site == null ||
                url == null)
            {
                return [copy];
            }

            return
            [
                copy,
                new($"View on {site.Name}", () => openSite(url))
                {
                    Glyph = Icons.Links
                }
            ];
        }
    }

    /// <summary>
    /// Create pull request, for the branch the bar shows, named as its site names one, where it
    /// would create one, or would ask to push the branch first, which it ends with an ellipsis
    /// for: see <see cref="PullRequest"/>. Null where it would only say why it can't, for the
    /// menus to leave it out.
    /// </summary>
    internal QuickMenu.Command? PullRequestCommand()
    {
        if (gitState is not { } state ||
            siteLabel.Tag is not RemoteSite site)
        {
            return null;
        }

        var text = $"Create {site.PullRequestName}";
        return PullRequest.For(state, site) switch
        {
            PullRequest.Open => new(text, CreatePullRequest)
            {
                Glyph = Icons.PullRequest,
                Shortcut = "Ctrl+R"
            },
            PullRequest.PushFirst => new($"{text}…", CreatePullRequest)
            {
                Glyph = Icons.PullRequest,
                Shortcut = "Ctrl+R"
            },
            _ => null
        };
    }

    /// <summary>
    /// Says why there is no pull request to create, for Ctrl+R. Tests listen in.
    /// </summary>
    internal Action<string, string> Tell;

    /// <summary>
    /// Ctrl+R, or Create pull request on the branch's right-click menu: the page on the site
    /// origin is on that creates one from the branch the bar shows, opened in the browser. A
    /// branch tracking none is pushed first, asked as the bar's parts ask, in the Actions panel,
    /// and the page opens once it is on origin. Otherwise it says why there is none to create.
    /// Outside a repository, nothing.
    /// </summary>
    public void CreatePullRequest()
    {
        if (gitRepo is not { } repo ||
            gitState is not { } state)
        {
            return;
        }

        var site = siteLabel.Tag as RemoteSite;
        switch (PullRequest.For(state, site))
        {
            case PullRequest.Open open:
                openSite(open.Url);
                return;
            case PullRequest.PushFirst push when site != null:
                if (GitStatusActions.Ask(window, PullRequest.PushFirstQuestion(push.Branch, state, site)) == GitStatusActions.Choice.Push)
                {
                    _ = PushThenCreate(repo, push.Branch, site);
                }

                return;
            case PullRequest.Cannot cannot:
                Tell($"Create {site?.PullRequestName ?? "pull request"}", cannot.Why);
                return;
        }
    }

    // Published as Push publishes a branch tracking none, to a branch of its name on origin, so
    // the page is the one for that.
    async Task PushThenCreate(string repo, string branch, RemoteSite site)
    {
        var entry = await ActionLog.Run(
            "Git push",
            [repo],
            async (progress, cancel) =>
            {
                var results = await CodeCommands.Push([repo], null, progress, cancel);
                // Before the entry ends, which has the windows read the states again, as the bar's
                // own Push does.
                GitCache.MarkStale([repo]);
                return CodeCommands.GitOutcome(results);
            });
        if (entry.State == ActionLog.State.Succeeded &&
            !window.IsDisposed)
        {
            openSite(site.PullRequestUrl(branch));
        }
    }

    // At the pointer, what is picked run once the menu has closed, as QuickMenu does.
    void ShowItemMenu(ToolStripItem item, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right ||
            CommandsFor(item) is not { Count: > 0 } commands)
        {
            return;
        }

        Action? chosen = null;
        var menu = QuickMenu.Build(commands, null, _ => chosen = _);
        menu.Closed += (_, _) => menu.BeginInvoke(
            () =>
            {
                menu.Dispose();
                chosen?.Invoke();
            });
        menu.Show(Cursor.Position);
    }

    static ToolStripStatusLabel Dot() =>
        new("·")
        {
            Margin = new(0, 3, 0, 2),
            Visible = false
        };

    void AddGitPart(GitState.PartKind kind, ToolStripItem item)
    {
        var dot = Dot();
        item.Visible = false;
        Strip.Items.Add(dot);
        Strip.Items.Add(item);
        gitParts[kind] = (dot, item);
    }

    ToolStripButton GitPartButton(string toolTip, GitState.PartKind kind)
    {
        var button = new ToolStripButton
        {
            Name = $"git{kind}",
            Margin = new(0, 1, 0, 0),
            AutoToolTip = false,
            ToolTipText = toolTip
        };
        button.Click += (_, _) => ActOnGit(kind);
        return button;
    }

    // The changes, ahead, behind or behind upstream clicked: asks what to do about them, pull
    // say. Those of the repository the bar shows, which is the Diff tab's too while it shows.
    void ActOnGit(GitState.PartKind kind)
    {
        if (gitRepo is { } repo &&
            gitState is { } state)
        {
            GitStatusActions.Act(window, kind, repo, state, settings.CodeDirectory);
        }
    }

    // A part for each of what the state of the repository says, the last spaced from what
    // follows. Each is set once, to what it is to be: hidden and shown again, the bar would be
    // laid out again for every status update, each change of selection say, closing a tooltip
    // open over it.
    void ShowGitParts(string? repo, GitState? state)
    {
        gitRepo = repo;
        gitState = state;
        var parts = state?.Parts() ?? [];
        var texts = parts.ToDictionary(_ => _.Kind, _ => _.Text);
        var tag = state?.Tag;
        ToolStripItem? last = null;
        if (parts.Count > 0)
        {
            last = ItemOf(parts[^1].Kind);
        }

        // Right after the branch, so last only where the branch would be.
        if (last == gitButton &&
            tag != null)
        {
            last = tagLabel;
        }

        Show(gitButton, texts.GetValueOrDefault(GitState.PartKind.Branch));
        tagDot.Visible = tag != null;
        Show(tagLabel, tag);
        foreach (var (kind, (dot, item)) in gitParts)
        {
            var text = texts.GetValueOrDefault(kind);
            dot.Visible = text != null;
            Show(item, text);
        }

        ToolStripItem ItemOf(GitState.PartKind kind)
        {
            if (kind == GitState.PartKind.Branch)
            {
                return gitButton;
            }

            return gitParts[kind].Item;
        }

        void Show(ToolStripItem item, string? text)
        {
            item.Visible = text != null;
            if (text == null)
            {
                return;
            }

            item.Text = text;

            // The chevron sits closer to the branch's right edge than text does to a part's, so
            // the dot after it gets the difference.
            var right = 0;
            if (item == last)
            {
                right = Scaled(12);
            }
            else if (item == gitButton)
            {
                right = Scaled(4);
            }

            item.Margin = item.Margin with
            {
                Right = right
            };
        }
    }

    /// <summary>
    /// Ctrl+B, or the branch clicked: the branches of the repository the bar shows, above the
    /// branch, found as they open. Outside a repository, nothing.
    /// </summary>
    public void ShowBranches()
    {
        if (!gitButton.Visible ||
            gitButton.Owner == null ||
            gitRepo is not { } repo ||
            Environment.TickCount64 - branchesClosedAt < 500)
        {
            return;
        }

        var state = gitState;
        var picker = BranchPicker.Show(
            window,
            gitButton.Owner.RectangleToScreen(gitButton.Bounds),
            Task.Run(() => GitBranches.Read(repo, Cancel.None)),
            branch => CodeMenu.SwitchBranch(window, repo, state, branch),
            name => CodeMenu.NewBranch(window, repo, name),
            (listing, branch) => CodeMenu.BranchCommands(window, repo, listing, branch, openTab));
        picker.FormClosed += (_, _) => branchesClosedAt = Environment.TickCount64;
    }

    void OnUpdatesChanged()
    {
        if (window.IsDisposed)
        {
            return;
        }

        if (window.InvokeRequired)
        {
            window.BeginInvoke(ShowUpdate);
            return;
        }

        ShowUpdate();
    }

    void ShowUpdate()
    {
        var available = updates.Available;
        updateLabel.Visible = available != null;
        if (available != null)
        {
            updateLabel.Text = $"Lode {available.ToNormalizedString()} is available";
        }
    }

    public void Dispose()
    {
        updates.Changed -= OnUpdatesChanged;
        tip.Dispose();
    }
}
