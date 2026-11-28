/// <summary>
/// Lode's options, a page each for General, View, Button actions, Menu actions, Search, Code,
/// Promoted files, File Explorer, Links and Updates, picked from the list down the left, as
/// Windows 11's Settings does. A change applies straight away.
/// </summary>
/// <remarks>
/// Laid out by docking: the dialog is a fixed size, the list docked down its left and Close along
/// its bottom, and the page picked fills the rest. A page's parts are docked one under another,
/// so its text wraps at the page's width. Docking takes no notice of margins, so a part's
/// padding is the room round it.
/// </remarks>
class OptionsForm : Form
{
    ExplorerReplacement explorerReplacement;

    Label explorerStatus = new WrappingLabel
    {
        Name = "explorerStatus",
        Padding = new(3, 0, 3, 6)
    };

    Button replaceButton = new()
    {
        Name = "replaceExplorer",
        Text = "Replace File Explorer",
        AutoSize = true,
        Padding = new(6, 0, 6, 0)
    };

    Button restoreButton = new()
    {
        Name = "restoreExplorer",
        Text = "Restore File Explorer",
        AutoSize = true,
        Padding = new(6, 0, 6, 0)
    };

    CheckBox showHidden = new()
    {
        Name = "showHidden",
        Text = "Show hidden items (Ctrl+H)",
        AutoSize = true,
        Margin = new(3, 0, 3, 9)
    };

    CheckBox showSystem = new()
    {
        Name = "showSystem",
        Text = "Show system files",
        AutoSize = true,
        Margin = new(3, 0, 3, 0)
    };

    CheckBox expandToOpenFolder = new()
    {
        Name = "expandToOpenFolder",
        Text = "Expand the folder tree to the open folder",
        AutoSize = true,
        Margin = new(3, 0, 3, 0)
    };

    CheckBox showRecent = new()
    {
        Name = "showRecent",
        Text = "Show recent folders in the folder tree",
        AutoSize = true,
        Margin = new(3, 0, 3, 0)
    };

    TextBox defaultDirectory = new()
    {
        Name = "defaultDirectory",
        ReadOnly = true,
        PlaceholderText = "Your user folder",
        Width = 300
    };

    Button browseDefaultDirectory = new()
    {
        Name = "browseDefaultDirectory",
        Text = "Browse...",
        AutoSize = true
    };

    Button resetDefaultDirectory = new()
    {
        Name = "resetDefaultDirectory",
        Text = "Reset",
        AutoSize = true
    };

    CheckBox closeUnusedTabs = new()
    {
        Name = "closeUnusedTabs",
        Text = "Close tabs not looked at for",
        AutoSize = true
    };

    NumericUpDown unusedTabHours = new()
    {
        Name = "unusedTabHours",
        AccessibleName = "Hours a tab is not looked at before it closes",
        Minimum = 1,
        Maximum = 999,
        Width = 60
    };

    Label unusedTabHoursUnit = new()
    {
        Text = "hours",
        AutoSize = true
    };

    TextBox codeDirectory = new()
    {
        Name = "codeDirectory",
        ReadOnly = true,
        PlaceholderText = "None",
        Width = 300
    };

    Button browseCodeDirectory = new()
    {
        Name = "browseCodeDirectory",
        Text = "Browse...",
        AutoSize = true
    };

    Button clearCodeDirectory = new()
    {
        Name = "clearCodeDirectory",
        Text = "Clear",
        AutoSize = true
    };

    /// <summary>
    /// Asks for the code folder. Replaced in tests, which can't answer a dialog.
    /// </summary>
    internal Func<string?> BrowseForCodeDirectory;

    /// <summary>
    /// Asks whether to use the folder as the code folder, saying what watching it means.
    /// Replaced in tests.
    /// </summary>
    internal Func<string, bool> ConfirmCodeDirectory;

    CheckBox searchIgnored = new()
    {
        Name = "searchIgnored",
        Text = "Search what git ignores too",
        AutoSize = true,
        Margin = new(3, 0, 3, 3)
    };

    CheckBox purgePermanently = new()
    {
        Name = "purgePermanently",
        Text = "Delete them permanently instead, which is faster",
        AutoSize = true,
        Margin = new(3, 0, 3, 3)
    };

    CheckBox fetchInBackground = new()
    {
        Name = "fetchInBackground",
        Text = "Fetch the repositories open in tabs every",
        AutoSize = true
    };

    NumericUpDown fetchMinutes = new()
    {
        Name = "fetchMinutes",
        AccessibleName = "Minutes between fetches of each repository open in a tab",
        Minimum = BackgroundFetch.MinimumMinutes,
        Maximum = BackgroundFetch.MaximumMinutes,
        Width = 60
    };

    Label fetchMinutesUnit = new()
    {
        Text = "minutes",
        AutoSize = true
    };

    // One pattern a line. See PromotedFiles.
    TextBox promotedFiles = new()
    {
        Name = "promotedFiles",
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "*.slnx",
        Width = 300,
        Height = 150
    };

    // The patterns windows were last told of. Typing changes the setting at once, but the
    // windows read their folders again only once the box is left, or Options closes, rather than
    // for every key.
    List<string> promotedApplied;

    // Shows the shield Windows puts on buttons that ask for administrator permission.
    Button excludeFromDefender = new()
    {
        Name = "excludeFromDefender",
        Text = "Exclude from Defender…",
        AutoSize = true,
        FlatStyle = FlatStyle.System,
        Padding = new(6, 0, 6, 0),
        Anchor = AnchorStyles.Left
    };

    /// <summary>
    /// Asks whether to exclude the folder from Defender, saying why. Replaced in tests.
    /// </summary>
    internal Func<string, bool> ConfirmDefenderExclusion;

    /// <summary>
    /// Excludes the folder from Defender. Replaced in tests, which must never change Defender.
    /// </summary>
    internal Func<string, Task<DefenderExclusion.Outcome>> ExcludeFromDefender = DefenderExclusion.Exclude;

    /// <summary>
    /// Says how excluding the folder went. Replaced in tests.
    /// </summary>
    internal Action<string, DefenderExclusion.Outcome> ReportDefenderExclusion;

    NumericUpDown contextLines = new()
    {
        Name = "contextLines",
        AccessibleName = "Lines shown either side of each match",
        Minimum = 0,
        Maximum = 20,
        Width = 60
    };

    Label contextLinesUnit = new()
    {
        Text = "lines either side of each match",
        AutoSize = true
    };

    Label windowsSearchStatus = new WrappingLabel
    {
        Name = "windowsSearchStatus",
        Padding = new(3, 0, 3, 6)
    };

    // With the shield, as each asks for administrator permission.
    Button turnOffWindowsSearch = new()
    {
        Name = "turnOffWindowsSearch",
        Text = "Turn off…",
        AutoSize = true,
        FlatStyle = FlatStyle.System,
        Padding = new(6, 0, 6, 0)
    };

    Button turnOnWindowsSearch = new()
    {
        Name = "turnOnWindowsSearch",
        Text = "Turn on…",
        AutoSize = true,
        FlatStyle = FlatStyle.System,
        Padding = new(6, 0, 6, 0)
    };

    // How the Windows Search service is: read again after each change, and whenever the page
    // shows, as it may have been changed outside Lode meanwhile.
    Func<WindowsSearchService.State> readWindowsSearch;

    /// <summary>
    /// Asks whether to turn Windows Search off, or on, saying what that does. Replaced in tests.
    /// </summary>
    internal Func<bool, bool> ConfirmWindowsSearch;

    /// <summary>
    /// Turns Windows Search on, or off. Replaced in tests, which must never change the service.
    /// </summary>
    internal Func<bool, Task<Elevated.Outcome>> ChangeWindowsSearch = on =>
    {
        if (on)
        {
            return WindowsSearchService.TurnOn();
        }

        return WindowsSearchService.TurnOff();
    };

    /// <summary>
    /// Says how turning Windows Search on, or off, went. Replaced in tests.
    /// </summary>
    internal Action<bool, Elevated.Outcome> ReportWindowsSearch;

    RepositoryLinks repositoryLinks;

    // For each kind of link, what opens it now, and the buttons that take it over, revert it, and
    // open Windows' Default apps to choose Lode for it.
    List<(RepositoryLinks.Handler Handler, Label Status, Button Take, Button Revert, Button Choose)> linkRows = [];

    /// <summary>
    /// Opens a Settings page. Replaced in tests, which must never open Settings.
    /// </summary>
    internal Action<string> OpenSettings = page =>
    {
        using var process = Process.Start(new ProcessStartInfo(page)
        {
            UseShellExecute = true
        });
    };

    Updates updates;

    Label updateStatus = new WrappingLabel
    {
        Name = "updateStatus",
        Padding = new(3, 0, 3, 6)
    };

    Button applyUpdate = new()
    {
        Name = "applyUpdate",
        Text = "Update and restart",
        AutoSize = true,
        Padding = new(6, 0, 6, 0)
    };

    Button checkForUpdates = new()
    {
        Name = "checkForUpdates",
        Text = "Check now",
        AutoSize = true,
        Padding = new(6, 0, 6, 0)
    };

    /// <summary>
    /// Starts the update. Replaced in tests, which must never update anything.
    /// </summary>
    internal Action StartUpdate;

    /// <summary>
    /// The update was started, and waits for Lode to exit, which is the caller's to do once this
    /// has closed.
    /// </summary>
    public bool UpdateStarted { get; private set; }

    Settings settings;
    IReadOnlyList<TextEditors.Editor> editors;

    // An entry in the text editor list. A null path is "Browse…".
    sealed record EditorItem(string Text, string? Path)
    {
        public override string ToString() => Text;
    }

    // In the order of Theme's values.
    ComboBox theme = new()
    {
        Name = "theme",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 200,
        Items =
        {
            "Same as Windows",
            "Light",
            "Dark"
        }
    };

    // In the order of DiffPlacement's values.
    ComboBox diffPlacement = new()
    {
        Name = "diffPlacement",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300,
        Items =
        {
            "A Diff tab, beside the folders' tabs",
            "A window of their own",
            "Over the folder list",
            "The text editor, or git's diff tool"
        }
    };

    ComboBox textEditor = new()
    {
        Name = "textEditor",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300
    };

    int textEditorIndex;

    PageList pageList = new()
    {
        Name = "pages",
        AutoSize = true,
        Dock = DockStyle.Left
    };

    // The page picked, filling what the list and Close leave, a little in from the list.
    Panel host = new()
    {
        Dock = DockStyle.Fill,
        Padding = new(15, 6, 0, 0)
    };

    // In the list's order, only the one picked there shown.
    List<Panel> pages = [];

    // The actions pages' tables, for the dialog to be made wide enough for.
    List<HeadedTable> headedTables = [];

    // A page's name over it, and the headings of its sections.
    Font headingFont;
    Font sectionFont;

    /// <summary>
    /// A setting that changes how windows look was changed, with what of them it changes.
    /// </summary>
    public event Action<SettingsChange>? SettingsChanged;

    /// <param name="editors">The text editors installed, Notepad last, from
    /// <see cref="TextEditors.Find()"/>. Passed in so tests show a fixed list.</param>
    /// <param name="updates">Whether there is a newer Lode, <see cref="Updates.Instance"/>.</param>
    /// <param name="repositoryLinks">Takes over links for GitHub Desktop and Visual Studio. By
    /// default for the same classes and copy of Lode as <paramref name="explorerReplacement"/>.</param>
    /// <param name="readWindowsSearch">How the Windows Search service is. By default as this
    /// machine has it; passed in so tests show a fixed one.</param>
    public OptionsForm(
        ExplorerReplacement explorerReplacement,
        Settings settings,
        IReadOnlyList<TextEditors.Editor> editors,
        Updates updates,
        RepositoryLinks? repositoryLinks = null,
        Func<WindowsSearchService.State>? readWindowsSearch = null)
    {
        this.explorerReplacement = explorerReplacement;
        this.repositoryLinks = repositoryLinks ?? RepositoryLinks.Like(explorerReplacement);
        this.readWindowsSearch = readWindowsSearch ?? WindowsSearchService.Read;
        contextLines.Value = Math.Clamp(settings.FindContextLines, (int) contextLines.Minimum, (int) contextLines.Maximum);
        contextLines.ValueChanged += (_, _) => settings.FindContextLines = (int) contextLines.Value;
        ConfirmWindowsSearch = ShowWindowsSearchPrompt;
        ReportWindowsSearch = ShowWindowsSearchOutcome;
        turnOffWindowsSearch.Click += (_, _) => OnChangeWindowsSearch(false);
        turnOnWindowsSearch.Click += (_, _) => OnChangeWindowsSearch(true);
        turnOffWindowsSearch.HandleCreated += (_, _) => SendMessage(turnOffWindowsSearch.Handle, buttonSetShield, IntPtr.Zero, 1);
        turnOnWindowsSearch.HandleCreated += (_, _) => SendMessage(turnOnWindowsSearch.Handle, buttonSetShield, IntPtr.Zero, 1);
        this.settings = settings;
        this.editors = editors;
        this.updates = updates;
        StartUpdate = updates.Apply;
        applyUpdate.Click += (_, _) => OnApplyUpdate();
        checkForUpdates.Click += (_, _) => _ = updates.Check();
        updates.Changed += OnUpdatesChanged;
        ActionLog.Changed += OnUpdatesChanged;
        BrowseForEditor = ShowBrowseDialog;
        BrowseForCodeDirectory = ShowCodeDirectoryDialog;
        ConfirmCodeDirectory = ShowCodeDirectoryPrompt;
        browseCodeDirectory.Click += (_, _) =>
        {
            // The one it is already is not asked about again.
            if (BrowseForCodeDirectory() is { } chosen &&
                !Locations.Same(chosen, settings.CodeDirectory) &&
                ConfirmCodeDirectory(chosen))
            {
                SetCodeDirectory(chosen);
            }
        };
        clearCodeDirectory.Click += (_, _) => SetCodeDirectory(null);
        ConfirmDefenderExclusion = ShowDefenderPrompt;
        ReportDefenderExclusion = ShowDefenderOutcome;
        excludeFromDefender.Click += (_, _) => OnExcludeFromDefender();
        excludeFromDefender.HandleCreated += (_, _) => SendMessage(excludeFromDefender.Handle, buttonSetShield, IntPtr.Zero, 1);
        UpdateCodeDirectory();
        FillTextEditors();
        textEditor.SelectedIndexChanged += (_, _) => TextEditorChosen();
        browseDefaultDirectory.Click += (_, _) => BrowseDefaultDirectory();
        resetDefaultDirectory.Click += (_, _) => SetDefaultDirectory(null);
        UpdateDefaultDirectory();
        closeUnusedTabs.Checked = settings.CloseUnusedTabs;
        unusedTabHours.Value = Math.Clamp(settings.UnusedTabHours, (int) unusedTabHours.Minimum, (int) unusedTabHours.Maximum);
        unusedTabHours.Enabled = settings.CloseUnusedTabs;
        closeUnusedTabs.CheckedChanged += (_, _) =>
        {
            settings.CloseUnusedTabs = closeUnusedTabs.Checked;
            unusedTabHours.Enabled = closeUnusedTabs.Checked;
        };
        unusedTabHours.ValueChanged += (_, _) => settings.UnusedTabHours = (int) unusedTabHours.Value;
        showHidden.Checked = settings.ShowHidden;
        showHidden.CheckedChanged += (_, _) =>
        {
            settings.ShowHidden = showHidden.Checked;
            SettingsChanged?.Invoke(SettingsChange.Listing);
        };
        showSystem.Checked = settings.ShowSystem;
        showSystem.CheckedChanged += (_, _) =>
        {
            settings.ShowSystem = showSystem.Checked;
            SettingsChanged?.Invoke(SettingsChange.Listing);
        };
        searchIgnored.Checked = settings.SearchIgnored;
        searchIgnored.CheckedChanged += (_, _) =>
        {
            settings.SearchIgnored = searchIgnored.Checked;
            SettingsChanged?.Invoke(SettingsChange.Listing);
        };
        purgePermanently.Checked = settings.PurgePermanently;
        purgePermanently.CheckedChanged += (_, _) => settings.PurgePermanently = purgePermanently.Checked;
        fetchInBackground.Checked = settings.FetchInBackground;
        fetchMinutes.Value = Math.Clamp(settings.FetchMinutes, (int) fetchMinutes.Minimum, (int) fetchMinutes.Maximum);
        fetchMinutes.Enabled = settings.FetchInBackground;
        fetchInBackground.CheckedChanged += (_, _) =>
        {
            settings.FetchInBackground = fetchInBackground.Checked;
            fetchMinutes.Enabled = fetchInBackground.Checked;
        };
        fetchMinutes.ValueChanged += (_, _) => settings.FetchMinutes = (int) fetchMinutes.Value;
        promotedFiles.Text = string.Join(Environment.NewLine, settings.PromotedFiles);
        promotedApplied = [.. settings.PromotedFiles];
        promotedFiles.TextChanged += (_, _) => settings.PromotedFiles = PatternLines(promotedFiles.Text);
        promotedFiles.Leave += (_, _) => ApplyPromotedFiles();
        // Its scroll bar dark in the dark theme, as the rest of Lode's are.
        promotedFiles.HandleCreated += (_, _) => SetWindowTheme(promotedFiles.Handle, ExplorerStyle.ControlTheme, null);
        theme.SelectedIndex = (int) settings.Theme;
        theme.SelectedIndexChanged += (_, _) =>
        {
            settings.Theme = (Theme) theme.SelectedIndex;
        };
        diffPlacement.SelectedIndex = (int) settings.DiffPlacement;
        diffPlacement.SelectedIndexChanged += (_, _) => settings.DiffPlacement = (DiffPlacement) diffPlacement.SelectedIndex;
        expandToOpenFolder.Checked = settings.ExpandToOpenFolder;
        expandToOpenFolder.CheckedChanged += (_, _) =>
        {
            settings.ExpandToOpenFolder = expandToOpenFolder.Checked;
            SettingsChanged?.Invoke(SettingsChange.TreeSelection);
        };
        showRecent.Checked = settings.ShowRecent;
        showRecent.CheckedChanged += (_, _) =>
        {
            settings.ShowRecent = showRecent.Checked;
            SettingsChanged?.Invoke(SettingsChange.Places);
        };

        SuspendLayout();
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        // Room for the pages, with some to spare, as text measures a little wider than it scales
        // on a screen above 100%. Each page's text wraps to the width left, and a page taller than
        // the room left scrolls.
        ClientSize = new(740, 600);
        Padding = new(9);
        headingFont = new(Font.FontFamily, Font.SizeInPoints * 1.5f);
        sectionFont = new(Font, FontStyle.Bold);

        replaceButton.Click += (_, _) => Change(explorerReplacement.Replace);
        restoreButton.Click += (_, _) => Change(explorerReplacement.Restore);

        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Margin = new(3, 12, 3, 3)
        };
        AcceptButton = closeButton;
        CancelButton = closeButton;

        // Close along the bottom, at its right.
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        bottom.Controls.Add(closeButton);

        AddPage("General", Icons.Options, BuildGeneralPage());
        AddPage("View", Icons.View, BuildViewPage());
        AddPage("Button actions", Icons.ButtonActions, BuildButtonActionsPage());
        AddPage("Menu actions", Icons.MenuActions, BuildMenuActionsPage());
        AddPage("Search", Icons.Search, BuildSearchPage());
        AddPage("Code", Icons.Code, BuildCodePage());
        AddPage("Promoted files", Icons.PromotedFiles, BuildPromotedFilesPage());
        AddPage("File Explorer", Icons.FileExplorer, BuildExplorerPage());
        AddPage("Links", Icons.Links, BuildLinksPage());
        AddPage("Updates", Icons.Updates, BuildUpdatesPage());
        pageList.SelectedChanged += ShowSelectedPage;
        ShowSelectedPage();

        // Docking goes from the last added: Close's row along the whole bottom first, then the
        // list down the left of what is above it, and the page picked fills the rest.
        Controls.Add(host);
        Controls.Add(pageList);
        Controls.Add(bottom);
        ResumeLayout(false);
        PerformLayout();

        UpdateExplorerState();
        UpdateLinkState();
        ShowUpdateState();
        UpdateWindowsSearchState();
    }

    // Made wide enough for the actions tables, then brought within the screen it opens on, once
    // it knows which.
    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        WidenForTables();
        FitWithin(Screen.FromControl(this).WorkingArea);
    }

    /// <summary>
    /// Widens the dialog by as much as the widest actions table runs past the room a page has,
    /// its scroll bar counted, staying centred where it was. Otherwise the scroll bar covers the
    /// last column, and the rows scroll sideways too. Each table is as wide as its longest name,
    /// which measures wider on a screen scaled up than the dialog scales. Measured against the
    /// room every page gets, as a page not shown isn't laid out, so has no width of its own yet.
    /// </summary>
    internal void WidenForTables()
    {
        var room = host.DisplayRectangle.Width;
        var overflow = headedTables.Max(_ => _.Overflow(room));
        if (overflow <= 0)
        {
            return;
        }

        Width += overflow;
        Left -= overflow / 2;
    }

    /// <summary>
    /// Makes the dialog no taller than <paramref name="area"/>, and moves it up within it. On a
    /// short screen, or one scaled up, it would run off the bottom, Close and all. Shorter, the
    /// page picked scrolls in the room left, or, an actions page, fits its table to it.
    /// </summary>
    internal void FitWithin(Rectangle area)
    {
        if (Height > area.Height)
        {
            Height = area.Height;
        }

        // Not one placed off every screen on purpose, as the tests place theirs.
        if (area.IntersectsWith(Bounds))
        {
            Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        }
    }

    /// <summary>
    /// Whether the page shown scrolls, as one taller than the room it has does.
    /// </summary>
    internal bool PagesScroll => pages[pageList.SelectedIndex].VerticalScroll.Visible;

    // A page: its name as a heading, then its parts, each docked under the one before, or along
    // the bottom where it says so. A part docked to fill, an actions page's table, takes what is
    // left between, and scrolls itself. Any other page scrolls where it is taller than the room
    // it has. Hidden until picked.
    void AddPage(string title, char glyph, Control[] parts)
    {
        Control[] stack =
        [
            new Label
            {
                Text = title,
                AutoSize = true,
                Font = headingFont,
                Padding = new(1, 0, 3, 12)
            },
            .. parts
        ];
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = stack.All(_ => _.Dock != DockStyle.Fill),
            Visible = false
        };
        foreach (var part in stack.Where(_ => _.Dock == DockStyle.None))
        {
            part.Dock = DockStyle.Top;
        }

        // Docking goes from the last added: those across the top first, from the top down, then
        // those along the bottom, the last lowest, and last the one filling what is left.
        page.Controls.AddRange(
        [
            .. stack.Where(_ => _.Dock == DockStyle.Fill),
            .. stack.Where(_ => _.Dock == DockStyle.Bottom),
            .. stack.Where(_ => _.Dock == DockStyle.Top).Reverse()
        ]);

        pages.Add(page);
        host.Controls.Add(page);
        pageList.Add(title, glyph);
    }

    void ShowSelectedPage()
    {
        for (var index = 0; index < pages.Count; index++)
        {
            pages[index].Visible = index == pageList.SelectedIndex;
        }

        // The service may have been changed outside Lode since the page last showed.
        if (pageList.PageText(pageList.SelectedIndex) == "Search")
        {
            UpdateWindowsSearchState();
        }

        // Each page from its top, rather than as far down as it was last scrolled.
        pages[pageList.SelectedIndex].AutoScrollPosition = Point.Empty;
    }

    /// <summary>
    /// Shows the page named <paramref name="title"/>, as picking it in the list does.
    /// </summary>
    internal void ShowPage(string title)
    {
        for (var index = 0; index < pageList.Count; index++)
        {
            if (pageList.PageText(index) == title)
            {
                pageList.SelectedIndex = index;
                return;
            }
        }

        throw new ArgumentException($"Options has no {title} page.", nameof(title));
    }

    // Ctrl+Tab and Ctrl+Shift+Tab, or Ctrl+Page Down and Up, go through the pages, as they go
    // through a dialog's tabs.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        var step = keyData switch
        {
            (Keys.Control | Keys.Tab) or (Keys.Control | Keys.PageDown) => 1,
            (Keys.Control | Keys.Shift | Keys.Tab) or (Keys.Control | Keys.PageUp) => -1,
            _ => 0
        };
        if (step == 0)
        {
            return base.ProcessCmdKey(ref message, keyData);
        }

        pageList.SelectedIndex = (pageList.SelectedIndex + step + pageList.Count) % pageList.Count;
        return true;
    }

    // A part of a page: a heading, what it is for, then its controls, the last with the room
    // under it before the next part.
    Control[] Section(string heading, string description, params Control[] controls)
    {
        var last = controls[^1];
        last.Padding = last.Padding with
        {
            Bottom = last.Padding.Bottom + 15
        };
        return
        [
            new Label
            {
                Text = heading,
                AutoSize = true,
                Font = sectionFont,
                Padding = new(3, 0, 3, 3)
            },
            Description(description, new(3, 0, 3, 6)),
            .. controls
        ];
    }

    static Label Description(string text, Padding padding) =>
        new WrappingLabel
        {
            Text = text,
            Padding = padding
        };

    // Grey text under a check box, lined up with the box's text.
    static Label Note(string text) =>
        new WrappingLabel
        {
            Text = text,
            Padding = new(21, 0, 3, 9),
            ForeColor = SystemColors.GrayText
        };

    // A table row rather than a flow, so each control is centred on the row's middle and a text
    // box lines up with the taller buttons beside it. Docked across a page, it keeps its
    // controls their own size, at its left, where docked themselves they would be as wide as the
    // page. Their margins count in it.
    static TableLayoutPanel Row(params Control[] controls)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = controls.Length,
            RowCount = 1,
            Margin = Padding.Empty
        };
        for (var column = 0; column < controls.Length; column++)
        {
            controls[column].Anchor = AnchorStyles.Left;
            row.Controls.Add(controls[column], column, 0);
        }

        return row;
    }

    Control[] BuildGeneralPage() =>
    [
        .. Section(
            "Default folder",
            "The folder new tabs open in, and new windows given no folder to show. Reset goes " +
            "back to your user folder.",
            Row(defaultDirectory, browseDefaultDirectory, resetDefaultDirectory)),
        .. Section(
            "Text editor",
            "Edit, on the right-click menu of a text file, opens it in this editor.",
            Row(textEditor)),
        .. Section(
            "Changes",
            "Where a file picked in the Changes or History pane shows its changes, drawn as " +
            "GitHub Desktop draws them.",
            Row(diffPlacement)),
        .. Section(
            "Tabs",
            "Other apps opening folders in Lode leave a tab for each. Lode can close those you " +
            "haven't looked at for a while, once you have gone to another app. It never closes " +
            "the tab a window shows, and Reopen closed tab (Ctrl+Shift+T) brings the others back.",
            Row(closeUnusedTabs, unusedTabHours, unusedTabHoursUnit))
    ];

    Control[] BuildViewPage() =>
    [
        .. Section(
            "Theme",
            "Light or dark, or whichever Windows' apps are set to. Lode takes it on when it next " +
            "starts: choose Exit from its icon in the notification area, then open it again.",
            Row(theme)),
        Row(showHidden),
        Row(showSystem),
        Note("Windows, Program Files, other people's folders under Users, and the files Windows protects."),
        Row(expandToOpenFolder),
        Note(
            "The tree opens out down to the folder the tab shows, and selects it, as File " +
            "Explorer's does only with Expand to open folder on, which it is not by default. Off, " +
            "the tree stays as you leave it, and selects the folder only where it already shows."),
        Row(showRecent),
        Note(
            "Pinned folders are the ones pinned to Quick access in File Explorer. Recent folders " +
            "are the ones Explorer adds there because you use them often.")
    ];

    Control[] BuildButtonActionsPage() =>
        BuildActionsPage(
            description:
            "Show each action as a button on the bar under the address bar, on its own or nested " +
            "in its parent's drop-down. The rest are under See more, as are the buttons a narrow " +
            "window has no room for. Reset puts them back as they were at first.",
            boxName: "button",
            where: "on the bar",
            shows: settings.ShowsAsButton,
            nests: settings.NestsButton,
            place: settings.SetButtonPlacement,
            changed: settings.ButtonPlacementsChanged,
            reset: settings.ResetButtonPlacements);

    Control[] BuildMenuActionsPage() =>
        BuildActionsPage(
            description:
            "Show each action in the right-click menus, on its own or nested in its parent's " +
            "submenu. The menus only offer what applies to what was clicked. Reset puts them back " +
            "as they were at first.",
            boxName: "menu",
            where: "in the right-click menu",
            shows: settings.ShowsInMenu,
            nests: settings.NestsInMenu,
            place: settings.SetMenuPlacement,
            changed: settings.MenuPlacementsChanged,
            reset: settings.ResetMenuPlacements);

    Control[] BuildCodePage() =>
    [
        .. Section(
            "Code folder",
            "The folder your git repositories are in. Folders in it get a Git menu (pull, switch " +
            "to default branch, push, discard, purge) and Update NuGets on their right-click menu. " +
            "Lode watches it while it runs, so Windows won't let a folder it is in be renamed or " +
            "moved until it is cleared here, or Lode exits.",
            Row(codeDirectory, browseCodeDirectory, clearCodeDirectory)),
        .. Section(
            "Fetch",
            "In the background, as GitHub Desktop does, never asking to sign in.",
            Row(fetchInBackground, fetchMinutes, fetchMinutesUnit)),
        .. Section(
            "Git purge",
            "Git purge deletes every file and folder git doesn't track, ignored ones " +
            "included, such as bin and obj. It moves them to the Recycle Bin.",
            Row(purgePermanently)),
        .. Section(
            "Microsoft Defender",
            "Defender scans every file builds and git write in the code folder, which slows " +
            "them down. Excluding the folder stops that, as Rider suggests, and removes the " +
            "exclusions under it, which it makes redundant. Windows asks for administrator " +
            "permission.",
            Row(excludeFromDefender))
    ];

    Control[] BuildPromotedFilesPage() =>
    [
        Description(
            "At a git repository's root, Lode also lists the files these name in the folders " +
            @"just below it, as shortcuts with the arrow, so src\Lode.slnx opens from the root " +
            "without going into src. Rename, Cut and Delete aren't offered on them there.",
            new(3, 0, 3, 9)),
        Description(
            "One a line: a whole name, or one with a *, before which is how the name starts, and " +
            "after which how it ends, as in *.slnx or Directory.*.props. Case doesn't matter.",
            new(3, 0, 3, 9)),
        Row(promotedFiles)
    ];

    // The patterns typed, one a line, without the blank lines.
    static List<string> PatternLines(string text) =>
        text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // Tells the windows of patterns changed since they were last told, to read their folders
    // again.
    void ApplyPromotedFiles()
    {
        if (settings.PromotedFiles.SequenceEqual(promotedApplied))
        {
            return;
        }

        promotedApplied = [.. settings.PromotedFiles];
        SettingsChanged?.Invoke(SettingsChange.Listing);
    }

    Control[] BuildSearchPage() =>
    [
        .. Section(
            "Search box",
            "The search box at the top right finds what is named like what is typed, in the " +
            "folder open and every folder beneath it. In a repository it leaves out what git " +
            "ignores, bin and node_modules say, as Find in Files does, and never goes into .git.",
            Row(searchIgnored)),
        .. Section(
            "Find in Files",
            "Ctrl+Shift+F searches what files hold, in the folder open, leaving out what git " +
            "ignores. With Context on in its strip, it shows lines either side of each match.",
            Row(contextLines, contextLinesUnit)),
        .. Section(
            "Windows Search",
            "Windows Search indexes your files in the background, for Start's search and " +
            "Explorer's. Lode doesn't use it: its search reads the folders itself. Turning it " +
            "off stops the indexing for good, but Start, Settings, Outlook and OneNote then " +
            "find no files, and Explorer's search becomes slower. Turn on starts it again as " +
            "Windows has it. Windows asks for administrator permission.",
            windowsSearchStatus,
            Row(turnOffWindowsSearch, turnOnWindowsSearch))
    ];

    void UpdateWindowsSearchState()
    {
        var state = readWindowsSearch();
        windowsSearchStatus.Text = WindowsSearchService.Describe(state);
        var installed = state.Startup != WindowsSearchService.Startup.Missing;
        turnOffWindowsSearch.Enabled = installed && !state.IsOff;
        turnOnWindowsSearch.Enabled = installed && !state.IsOn;
    }

    async void OnChangeWindowsSearch(bool on)
    {
        if (!ConfirmWindowsSearch(on))
        {
            return;
        }

        turnOffWindowsSearch.Enabled = false;
        turnOnWindowsSearch.Enabled = false;
        try
        {
            var outcome = await ChangeWindowsSearch(on);
            if (!IsDisposed)
            {
                ReportWindowsSearch(on, outcome);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                UpdateWindowsSearchState();
            }
        }
    }

    bool ShowWindowsSearchPrompt(bool on)
    {
        TaskDialogPage page;
        TaskDialogButton change;
        if (on)
        {
            change = new("Turn on");
            page = new()
            {
                Caption = "Lode",
                Heading = "Turn Windows Search on?",
                Text = "Windows Search starts again, and from then on shortly after Windows does, " +
                       "as Windows has it. It indexes your files again, which takes a while at " +
                       "first.\n\nWindows will ask for administrator permission."
            };
        }
        else
        {
            change = new("Turn off");
            page = new()
            {
                Caption = "Lode",
                Heading = "Turn Windows Search off?",
                Text = "Windows Search stops, and doesn't start again, even after a restart, until " +
                       "it is turned on here. Start, Settings, Outlook and OneNote then find no " +
                       "files, and Explorer's search becomes slower. Lode's search doesn't use it." +
                       "\n\nWindows will ask for administrator permission."
            };
        }

        page.Icon = TaskDialogIcon.ShieldBlueBar;
        page.Buttons = [change, TaskDialogButton.Cancel];
        page.DefaultButton = change;
        return TaskDialog.ShowDialog(this, page) == change;
    }

    void ShowWindowsSearchOutcome(bool on, Elevated.Outcome outcome)
    {
        if (outcome.Cancelled ||
            outcome.Succeeded)
        {
            return;
        }

        var change = "off";
        if (on)
        {
            change = "on";
        }

        MessageBox.Show(this, $"Lode couldn't turn Windows Search {change}: {outcome.Error}", "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    Control[] BuildLinksPage()
    {
        var controls = new List<Control>
        {
            Description(
                "Open the repository from the code folder in Lode when a site's button would hand " +
                "it to a desktop app, with the file the link names selected. A link for a " +
                "repository that isn't in the code folder offers to clone it there, or to hand " +
                "the link to the app it was for. " +
                "Revert gives the links back to that app. Choose in Default apps keeps them with " +
                "Lode when that app takes them back. This applies to your Windows account only.",
                new(3, 0, 3, 3))
        };
        foreach (var handler in RepositoryLinks.All)
        {
            var status = new WrappingLabel
            {
                Name = $"linkStatus.{handler.Id}",
                Padding = new(3, 6, 3, 3)
            };
            var take = new Button
            {
                Name = $"takeLinks.{handler.Id}",
                Text = "Open in Lode",
                AccessibleName = $"Open {handler.Buttons} links in Lode",
                AutoSize = true,
                Padding = new(6, 0, 6, 0)
            };
            var revert = new Button
            {
                Name = $"revertLinks.{handler.Id}",
                Text = $"Revert to {handler.App}",
                AutoSize = true,
                Padding = new(6, 0, 6, 0)
            };
            var choose = new Button
            {
                Name = $"chooseLinks.{handler.Id}",
                Text = "Choose in Default apps",
                AccessibleName = $"Choose Lode for {handler.Buttons} links in Windows' Default apps",
                AutoSize = true,
                Padding = new(6, 0, 6, 0)
            };
            take.Click += (_, _) => ChangeLinks(() => repositoryLinks.Take(handler));
            revert.Click += (_, _) => ChangeLinks(() => repositoryLinks.GiveBack(handler));
            choose.Click += (_, _) => OnChooseLinks();
            linkRows.Add((handler, status, take, revert, choose));
            controls.Add(status);
            controls.Add(Row(take, revert, choose));
        }

        return controls.ToArray();
    }

    void ChangeLinks(Action change)
    {
        try
        {
            change();
        }
        catch (Exception exception)
            when (
                exception is
                    UnauthorizedAccessException or
                    SecurityException or
                    IOException)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateLinkState();
    }

    // Settings opens on Lode's page of Default apps. The choice made there is only seen once
    // Options is back in front.
    void OnChooseLinks()
    {
        try
        {
            OpenSettings(RepositoryLinks.DefaultAppsPage);
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateLinkState();
    }

    void UpdateLinkState()
    {
        foreach (var (handler, status, take, revert, choose) in linkRows)
        {
            var taken = repositoryLinks.IsTaken(handler);
            var here = repositoryLinks.IsTakenHere(handler);
            var chosen = repositoryLinks.IsChosen(handler);
            string opens;
            if (here)
            {
                opens = "Lode opens them.";
            }
            else if (taken)
            {
                opens = "Another copy of Lode opens them.";
            }
            else if (repositoryLinks.CurrentApp(handler) is { } app)
            {
                opens = $"{app} opens them.";
                if (repositoryLinks.IsTakenBack(handler))
                {
                    if (handler.TakesBack)
                    {
                        opens += $" It takes them back from Lode each time it starts, unless Lode is chosen for {handler.Scheme} links in Windows' Default apps.";
                    }
                    else
                    {
                        opens += " It took them back from Lode.";
                    }
                }
            }
            else
            {
                opens = "No app opens them.";
            }

            if (chosen)
            {
                opens += $" Lode is chosen for {handler.Scheme} links in Windows' Default apps, so they stay with it.";
            }
            else if (handler.TakesBack &&
                     taken)
            {
                opens += $" {handler.App} takes them back each time it starts, unless Lode is chosen for {handler.Scheme} links in Windows' Default apps.";
            }

            // An app picked in Windows' own settings is asked for the links ahead of the
            // command Lode writes.
            if (taken &&
                repositoryLinks.OtherChosenAppName(handler) is { } other)
            {
                opens += $" But Windows' Default apps has {other} chosen for {handler.Scheme} links, which comes first, so they still open there.";
            }

            status.Text = $"{handler.Buttons}: {opens}";
            take.Enabled = !here;
            revert.Enabled = taken || repositoryLinks.IsRegistered(handler);
            choose.Enabled = repositoryLinks.IsRegistered(handler) && !chosen;
        }
    }

    Control[] BuildExplorerPage() =>
    [
        Description(
            "Open folders, drives and Win+E in Lode instead of File Explorer. This applies to " +
            "your Windows account only. Explorer's own Open stays on every folder's " +
            "right-click menu, and the taskbar's File Explorer button still opens Explorer.",
            new(3, 0, 3, 9)),
        explorerStatus,
        Row(replaceButton, restoreButton)
    ];

    Control[] BuildUpdatesPage() =>
    [
        Description(
            "Lode asks NuGet for a newer version once a day, and shows one beside the free " +
            "space at the bottom of each window. Updating closes Lode, runs dotnet tool " +
            "update, and starts Lode again with the tabs it had.",
            new(3, 0, 3, 9)),
        updateStatus,
        Row(applyUpdate, checkForUpdates)
    ];

    void OnUpdatesChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(ShowUpdateState);
            return;
        }

        ShowUpdateState();
    }

    void ShowUpdateState()
    {
        // Not while an action runs: the update has Lode exit, which would stop it part way.
        var running = ActionLog.Entries.Any(_ => _.State == ActionLog.State.Running);
        updateStatus.Text = UpdateStatus(updates);
        if (running &&
            updates.Available != null)
        {
            updateStatus.Text += " It can be installed once the actions running have finished.";
        }

        applyUpdate.Enabled = updates.Available != null &&
                              !updates.Checking &&
                              !running;
        checkForUpdates.Enabled = updates is
        {
            Tool: not null,
            Checking: false
        };
    }

    /// <summary>
    /// Where the update stands, as the Updates page says it.
    /// </summary>
    internal static string UpdateStatus(Updates updates)
    {
        var current = updates.Current.ToNormalizedString();
        if (updates.Tool == null)
        {
            return $"This is Lode {current}, run from a build rather than installed as a dotnet " +
                   "tool, so it has nothing to update.";
        }

        if (updates.Checking)
        {
            return "Checking for a newer version…";
        }

        if (updates.Available is { } available)
        {
            return $"Lode {available.ToNormalizedString()} is available. This is {current}.";
        }

        if (updates.Error is { } error)
        {
            return $"Couldn't check for a newer version: {error}";
        }

        if (updates.LastChecked is { } lastChecked)
        {
            var local = TimeZoneInfo.ConvertTime(lastChecked, updates.LocalTimeZone);
            return $"Lode {current} is the newest version. Last checked {local:g}.";
        }

        return $"This is Lode {current}. It hasn't checked for a newer version yet.";
    }

    void OnApplyUpdate()
    {
        try
        {
            StartUpdate();
        }
        catch (Exception exception)
            when (
                exception is
                    Win32Exception or
                    InvalidOperationException)
        {
            MessageBox.Show(this, $"Lode couldn't start the update: {exception.Message}", "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UpdateStarted = true;
        Close();
    }

    void BrowseDefaultDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder new windows open in",
            UseDescriptionForTitle = true,
            InitialDirectory = settings.StartLocation()
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetDefaultDirectory(dialog.SelectedPath);
        }
    }

    void SetDefaultDirectory(string? path)
    {
        settings.DefaultDirectory = path;
        UpdateDefaultDirectory();
    }

    void UpdateDefaultDirectory()
    {
        defaultDirectory.Text = settings.DefaultDirectory ?? "";
        resetDefaultDirectory.Enabled = settings.DefaultDirectory != null;
    }

    /// <summary>
    /// A page of whether each action shows in one place, the actions bar or the right-click
    /// menus, and whether nested under its parent there, with Reset under it. A tick, or Reset,
    /// changes every window at once, and leaves where actions show in the other place as it is.
    /// </summary>
    /// <param name="boxName">Before each tick's name, button.cut and buttonNested.cut, and Reset's, buttonReset.</param>
    /// <param name="where">After each tick's accessible name: Cut on the bar.</param>
    /// <param name="place">Sets whether the action shows there, and is nested there.</param>
    /// <param name="changed">Whether any action shows there other than as it does at first.</param>
    /// <param name="reset">Puts every action there back as it was at first.</param>
    Control[] BuildActionsPage(string description, string boxName, string where, Func<string, bool> shows, Func<string, bool> nests, Action<string, bool, bool> place, Func<bool> changed, Action reset)
    {
        var resetButton = new Button
        {
            Name = $"{boxName}Reset",
            Text = "Reset",
            AutoSize = true,
            Enabled = changed()
        };
        // Each row's ticks set again from the settings, once Reset has changed them, while the
        // ticks' own changes are ignored.
        var refreshes = new List<Action>();
        var refreshing = false;

        // After a tick or Reset: Reset greyed out once there is nothing left to put back.
        void Placed()
        {
            resetButton.Enabled = changed();
            SettingsChanged?.Invoke(SettingsChange.Actions);
        }

        // Over names, so to their left, as they are.
        var parentHeading = ColumnHeading("Parent");
        parentHeading.Anchor = AnchorStyles.Left;
        parentHeading.Margin = new(0, 0, 12, 3);
        // The icon and name columns have none.
        Label?[] headings =
        [
            null,
            null,
            parentHeading,
            ColumnHeading("Show"),
            ColumnHeading("Nested")
        ];

        var table = new LaidOutTable
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = headings.Length,
            Margin = Padding.Empty
        };
        // Laid out once, when every row is in. The table sizes itself to what it holds, so each
        // control added would otherwise lay out all of it again, which for a couple of hundred
        // took most of the half second Options took to open.
        table.SuspendLayout();
        // The headings are above the table rather than in it, so they stay put as it scrolls.
        // This row, as wide as they are and no taller, keeps each column wide enough for its own.
        for (var column = 0; column < headings.Length; column++)
        {
            if (headings[column] is { } heading)
            {
                table.Controls.Add(new HeadingWidth(heading), column, 0);
            }
        }

        var iconSize = LogicalToDeviceUnits(16);
        var row = 1;
        foreach (var action in LodeActions.Offered())
        {
            var icon = new PictureBox
            {
                Image = Icons.Bitmap(action.Glyph, iconSize, SystemColors.ControlText),
                SizeMode = PictureBoxSizeMode.CenterImage,
                // In logical units, as the dialog scales what it holds to the screen itself. The
                // image is drawn at the screen's size already, so is not.
                Size = new(16 + 4, 16 + 4),
                Anchor = AnchorStyles.None,
                Margin = new(3, 1, 3, 1)
            };
            var name = new Label
            {
                Text = action.Text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new(0, 1, 12, 1)
            };
            var shown = new CheckBox
            {
                Name = $"{boxName}.{action.Id}",
                AccessibleName = $"{action.Text} {where}",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new(3, 1, 3, 1),
                Checked = shows(action.Id)
            };
            table.Controls.Add(icon, 0, row);
            table.Controls.Add(name, 1, row);
            table.Controls.Add(shown, 3, row);
            var id = action.Id;
            if (action.Parent is not { } parent)
            {
                shown.CheckedChanged += (_, _) =>
                {
                    if (refreshing)
                    {
                        return;
                    }

                    place(id, shown.Checked, false);
                    Placed();
                };
                refreshes.Add(() => shown.Checked = shows(id));
                row++;
                continue;
            }

            var nested = NestedCheckBox($"{boxName}Nested.{id}", $"{action.Text} under {parent.Text} {where}", nests(id), shown.Checked);
            EventHandler ticked = (_, _) =>
            {
                // Nesting only means anything where the action shows.
                nested.Enabled = shown.Checked;
                if (refreshing)
                {
                    return;
                }

                place(id, shown.Checked, nested.Checked);
                Placed();
            };
            shown.CheckedChanged += ticked;
            nested.CheckedChanged += ticked;
            refreshes.Add(() =>
            {
                shown.Checked = shows(id);
                nested.Checked = nests(id);
            });
            table.Controls.Add(
                new Label
                {
                    Text = parent.Text,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    ForeColor = SystemColors.GrayText,
                    Margin = new(0, 1, 12, 1)
                },
                2,
                row);
            table.Controls.Add(nested, 4, row);
            row++;
        }

        table.ResumeLayout();
        resetButton.Click += (_, _) =>
        {
            reset();
            refreshing = true;
            foreach (var refresh in refreshes)
            {
                refresh();
            }

            refreshing = false;
            Placed();
        };
        var headedTable = new HeadedTable(headings, table);
        headedTables.Add(headedTable);
        var resetRow = Row(resetButton);
        resetRow.Dock = DockStyle.Bottom;
        resetRow.Padding = new(0, 9, 0, 0);
        return
        [
            Description(description, new(3, 0, 3, 9)),
            headedTable,
            resetRow
        ];
    }

    static CheckBox NestedCheckBox(string name, string accessibleName, bool nested, bool shown) =>
        new()
        {
            Name = name,
            AccessibleName = accessibleName,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new(3, 1, 3, 1),
            Checked = nested,
            Enabled = shown
        };

    static Label ColumnHeading(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = SystemColors.GrayText,
            Margin = new(6, 0, 6, 3)
        };

    string? ShowCodeDirectoryDialog()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder your git repositories are in",
            UseDescriptionForTitle = true,
            InitialDirectory = settings.CodeDirectory ?? Locations.Home
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return dialog.SelectedPath;
    }

    // Lode watches the code folder, and Windows lets nobody rename or move a folder that one
    // being watched is in. Nor does Lode follow the code folder itself when something else
    // renames, moves or deletes it.
    bool ShowCodeDirectoryPrompt(string folder)
    {
        var use = new TaskDialogButton("Use folder");
        var page = new TaskDialogPage
        {
            Caption = "Lode",
            Heading = "Use this as the code folder?",
            Text = $"Lode watches {folder} for changes while it runs, to keep the Git column up to " +
                   "date. While it does:\n\n" +
                   "• Windows won't let a folder it is in be renamed or moved.\n" +
                   "• If it is renamed, moved or deleted itself, Lode goes on looking for it " +
                   "where it was.\n\n" +
                   "Before doing any of that, clear the code folder here in Options, or exit Lode.",
            Icon = TaskDialogIcon.Information,
            Buttons = {use, TaskDialogButton.Cancel},
            DefaultButton = use
        };
        return TaskDialog.ShowDialog(this, page) == use;
    }

    // The one it was is watched no longer, so nothing of Lode's holds it.
    void SetCodeDirectory(string? path)
    {
        if (settings.CodeDirectory is { } was &&
            !Locations.Same(was, path))
        {
            GitCache.LetGo(was);
        }

        settings.CodeDirectory = path;
        UpdateCodeDirectory();
    }

    void UpdateCodeDirectory()
    {
        codeDirectory.Text = settings.CodeDirectory ?? "";
        clearCodeDirectory.Enabled = settings.CodeDirectory != null;
        excludeFromDefender.Enabled = settings.CodeDirectory != null;
    }

    const int buttonSetShield = 0x160C;

    async void OnExcludeFromDefender()
    {
        if (settings.CodeDirectory is not { } folder ||
            !ConfirmDefenderExclusion(folder))
        {
            return;
        }

        excludeFromDefender.Enabled = false;
        try
        {
            var outcome = await ExcludeFromDefender(folder);
            if (!IsDisposed)
            {
                ReportDefenderExclusion(folder, outcome);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                UpdateCodeDirectory();
            }
        }
    }

    bool ShowDefenderPrompt(string folder)
    {
        var exclude = new TaskDialogButton("Exclude");
        var page = new TaskDialogPage
        {
            Caption = "Lode",
            Heading = "Exclude the code folder from Microsoft Defender?",
            Text = $"Defender scans every file builds and git write in {folder}, which slows them " +
                   "down. Excluding the folder stops that, as Rider suggests.\n\n" +
                   "Exclusions already under it become redundant, so they are removed. Windows " +
                   "will ask for administrator permission.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            Buttons = {exclude, TaskDialogButton.Cancel},
            DefaultButton = exclude
        };
        return TaskDialog.ShowDialog(this, page) == exclude;
    }

    void ShowDefenderOutcome(string folder, DefenderExclusion.Outcome outcome)
    {
        if (outcome.Cancelled)
        {
            return;
        }

        if (!outcome.Succeeded)
        {
            MessageBox.Show(this, $"Lode couldn't exclude {folder} from Defender: {outcome.Error}", "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var text = $"Defender no longer scans {folder}.";
        if (outcome.Removed.Count > 0)
        {
            text += $"\n\nRemoved {outcome.Removed.Count} exclusions under it, which this made redundant:\n" +
                    string.Join("\n", outcome.Removed);
        }

        MessageBox.Show(this, text, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // The editors found, then one chosen by browsing if that is what is set, then Browse….
    void FillTextEditors()
    {
        textEditor.Items.Clear();
        foreach (var editor in editors)
        {
            textEditor.Items.Add(new EditorItem(editor.Name, editor.Path));
        }

        var chosen = settings.TextEditor;
        if (chosen != null &&
            !editors.Any(_ => string.Equals(_.Path, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            textEditor.Items.Add(new EditorItem(Path.GetFileNameWithoutExtension(chosen), chosen));
        }

        textEditor.Items.Add(new EditorItem("Browse…", null));

        // Nothing chosen yet means the first editor found, as Edit uses.
        var index = 0;
        if (chosen != null)
        {
            index = textEditor.Items
                .Cast<EditorItem>()
                .ToList()
                .FindIndex(_ => string.Equals(_.Path, chosen, StringComparison.OrdinalIgnoreCase));
        }

        textEditorIndex = Math.Max(0, index);
        textEditor.SelectedIndex = textEditorIndex;
    }

    void TextEditorChosen()
    {
        if (textEditor.SelectedIndex == textEditorIndex ||
            textEditor.SelectedItem is not EditorItem item)
        {
            return;
        }

        if (item.Path != null)
        {
            settings.TextEditor = item.Path;
            textEditorIndex = textEditor.SelectedIndex;
            return;
        }

        var browsed = BrowseForEditor();
        if (browsed == null)
        {
            // Cancelled: back to what was chosen before.
            textEditor.SelectedIndex = textEditorIndex;
            return;
        }

        settings.TextEditor = browsed;
        FillTextEditors();
    }

    /// <summary>
    /// Asks for an editor's executable. Replaced in tests, which can't answer a dialog.
    /// </summary>
    internal Func<string?> BrowseForEditor;

    string? ShowBrowseDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a text editor",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return dialog.FileName;
    }

    // Written straight away rather than when the last window closes, so a change made here
    // survives Lode ending some other way.
    protected override void OnFormClosed(FormClosedEventArgs args)
    {
        base.OnFormClosed(args);
        ApplyPromotedFiles();
        settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            updates.Changed -= OnUpdatesChanged;
            ActionLog.Changed -= OnUpdatesChanged;
            headingFont.Dispose();
            sectionFont.Dispose();
        }
    }

    void Change(Action change)
    {
        try
        {
            change();
        }
        catch (Exception exception)
            when (
                exception is
                    UnauthorizedAccessException or
                    SecurityException or
                    IOException)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Apps find Lode's windows to show a file selected in only while Lode opens folders.
        ShellWindows.SyncAll();
        UpdateExplorerState();
    }

    void UpdateExplorerState()
    {
        var registered = explorerReplacement.RegisteredCommand;
        var here = explorerReplacement.IsActiveHere;
        var complete = explorerReplacement.IsComplete;
        if (registered == null)
        {
            explorerStatus.Text = "Currently File Explorer opens them.";
        }
        else if (complete)
        {
            explorerStatus.Text = "Currently Lode opens them.";
        }
        else if (here)
        {
            explorerStatus.Text = "Currently Lode opens them, except from apps that ask for File Explorer itself, such as GitHub Desktop. Replace again to cover those.";
        }
        else
        {
            explorerStatus.Text = $"Currently another copy of Lode opens them: {registered}";
        }

        // Replace stays available when another copy is registered, to point folders at this one,
        // and after a Replace by an older Lode, to add what that one left out.
        replaceButton.Enabled = !complete;
        restoreButton.Enabled = registered != null;
    }

    // A table under its column headings, as the actions are, filling what is left of its page
    // under what is above it. The rows scroll in that, the headings staying put above them, so
    // what each column is shows however far down they are scrolled.
    sealed class HeadedTable : TableLayoutPanel
    {
        LaidOutTable table;

        /// <summary>
        /// How far the table runs past <paramref name="room"/>, the width of its page, with the
        /// scroll bar it has, as its rows are many more than fit: none where it fits. As wide as
        /// it would make itself, as it hasn't been laid out where its page isn't shown.
        /// </summary>
        public int Overflow(int room) =>
            table.PreferredSize.Width + SystemInformation.VerticalScrollBarWidth - room;

        /// <param name="headings">By column, null for one without.</param>
        /// <param name="table">Its first row a <see cref="HeadingWidth"/> under each heading.</param>
        public HeadedTable(Control?[] headings, LaidOutTable table)
        {
            this.table = table;
            Dock = DockStyle.Fill;
            ColumnCount = 1;
            Margin = Padding.Empty;
            var header = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = headings.Length,
                RowCount = 1,
                Margin = Padding.Empty
            };
            header.SuspendLayout();
            for (var column = 0; column < headings.Length; column++)
            {
                header.ColumnStyles.Add(new(SizeType.Absolute, 0));
                if (headings[column] is { } heading)
                {
                    header.Controls.Add(heading, column, 0);
                }
            }

            // Each heading over its column, however wide the table makes that.
            void Align()
            {
                var widths = table.GetColumnWidths();
                for (var column = 0; column < widths.Length; column++)
                {
                    var style = header.ColumnStyles[column];
                    // Only where it differs, as each change lays the headings out again.
                    if ((int) style.Width != widths[column])
                    {
                        style.Width = widths[column];
                    }
                }
            }

            Align();
            header.ResumeLayout();
            table.LaidOut += Align;
            // As wide as the page, so the rows' scroll bar is at its right edge. The headings as
            // tall as they are, and the rows the rest.
            ColumnStyles.Add(new(SizeType.Percent, 100));
            RowStyles.Add(new(SizeType.AutoSize));
            RowStyles.Add(new(SizeType.Percent, 100));
            Controls.Add(header, 0, 0);
            Controls.Add(
                new Rows(table)
                {
                    Dock = DockStyle.Fill
                },
                0,
                1);
        }

        // Under the headings, scrolling the table where it runs past the room it has.
        sealed class Rows : Panel
        {
            public Rows(Control table)
            {
                AutoScroll = true;
                Margin = Padding.Empty;
                table.Location = Point.Empty;
                Controls.Add(table);
            }

            protected override void OnHandleCreated(EventArgs args)
            {
                base.OnHandleCreated(args);
                SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);
            }
        }
    }

    // Text docked across a page, as tall as it is wrapped at the page's width. Docking leaves a
    // label that sizes itself as it sizes itself, which is on one line, so this one doesn't, but
    // takes the height its text needs at the width docking gives it, again whenever that width,
    // or what it shows, changes.
    sealed class WrappingLabel : Label
    {
        protected override void OnSizeChanged(EventArgs args)
        {
            base.OnSizeChanged(args);
            Fit();
        }

        protected override void OnTextChanged(EventArgs args)
        {
            base.OnTextChanged(args);
            Fit();
        }

        protected override void OnFontChanged(EventArgs args)
        {
            base.OnFontChanged(args);
            Fit();
        }

        protected override void OnPaddingChanged(EventArgs args)
        {
            base.OnPaddingChanged(args);
            Fit();
        }

        void Fit()
        {
            var height = GetPreferredSize(new(Width, 0)).Height;
            if (Height != height)
            {
                Height = height;
            }
        }
    }

    // A table that says when it has laid out its columns, which Layout, raised just before, can't.
    sealed class LaidOutTable : TableLayoutPanel
    {
        public event Action? LaidOut;

        protected override void OnLayout(LayoutEventArgs args)
        {
            base.OnLayout(args);
            LaidOut?.Invoke();
        }
    }

    // In a table whose headings are above it rather than in it, as wide as the heading over its
    // column, margins and all, and no taller, so the column is wide enough for the heading.
    sealed class HeadingWidth : Control
    {
        Control heading;

        public HeadingWidth(Control heading)
        {
            this.heading = heading;
            AutoSize = true;
            Margin = Padding.Empty;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            new(heading.GetPreferredSize(Size.Empty).Width + heading.Margin.Horizontal, 0);
    }
}
