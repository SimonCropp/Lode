/// <summary>
/// Lode's options, a page each for General, View, Actions, Code, File Explorer, Links and Updates, picked
/// from the list down the left, as Windows 11's Settings does. A change applies straight away.
/// </summary>
class OptionsForm : Form
{
    // The widest a page's text runs before it wraps.
    const int textWidth = 440;

    ExplorerReplacement explorerReplacement;

    Label explorerStatus = new()
    {
        Name = "explorerStatus",
        AutoSize = true,
        MaximumSize = new(textWidth, 0),
        Margin = new(3, 0, 3, 6)
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

    // Shows the shield Windows puts on buttons that ask for administrator permission.
    Button excludeFromDefender = new()
    {
        Name = "excludeFromDefender",
        Text = "Exclude from Defender…",
        AutoSize = true,
        FlatStyle = FlatStyle.System,
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

    Label windowsSearchStatus = new()
    {
        Name = "windowsSearchStatus",
        AutoSize = true,
        MaximumSize = new(textWidth, 0),
        Margin = new(3, 0, 3, 6)
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

    // For each kind of link, what opens it now, and the buttons that take it over and revert it.
    List<(RepositoryLinks.Handler Handler, Label Status, Button Take, Button Revert)> linkRows = [];

    Updates updates;

    Label updateStatus = new()
    {
        Name = "updateStatus",
        AutoSize = true,
        MaximumSize = new(textWidth, 0),
        Margin = new(3, 0, 3, 6)
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
        Anchor = AnchorStyles.Top | AnchorStyles.Left,
        Margin = new(0, 0, 15, 0)
    };

    // In the list's order, only the one picked there shown.
    List<Control> pages = [];

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
        theme.SelectedIndex = (int) settings.Theme;
        theme.SelectedIndexChanged += (_, _) =>
        {
            settings.Theme = (Theme) theme.SelectedIndex;
        };
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
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
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
            Anchor = AnchorStyles.Right,
            Margin = new(3, 12, 3, 3)
        };
        AcceptButton = closeButton;
        CancelButton = closeButton;

        AddPage("General", Icons.Options, BuildGeneralPage());
        AddPage("View", Icons.View, BuildViewPage());
        AddPage("Actions", Icons.Actions, BuildActionsPage());
        AddPage("Search", Icons.Search, BuildSearchPage());
        AddPage("Code", Icons.Code, BuildCodePage());
        AddPage("File Explorer", Icons.FileExplorer, BuildExplorerPage());
        AddPage("Links", Icons.Links, BuildLinksPage());
        AddPage("Updates", Icons.Updates, BuildUpdatesPage());
        pageList.SelectedChanged += ShowSelectedPage;
        ShowSelectedPage();

        host = new()
        {
            AutoSize = true,
            Margin = new(0, 6, 0, 0)
        };
        foreach (var page in pages)
        {
            host.Controls.Add(page);
        }

        // The pages down the left, and beside them the one picked.
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Location = new(Padding.Left, Padding.Top)
        };
        layout.Controls.Add(pageList, 0, 0);
        layout.Controls.Add(host, 1, 0);
        layout.Controls.Add(closeButton, 0, 1);
        layout.SetColumnSpan(closeButton, 2);
        Controls.Add(layout);
        ResumeLayout(false);
        PerformLayout();

        UpdateExplorerState();
        UpdateLinkState();
        ShowUpdateState();
        UpdateWindowsSearchState();
    }

    PageHost host;

    // Brought within the screen it opens on, once it knows which.
    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        FitWithin(Screen.FromControl(this).WorkingArea);
    }

    /// <summary>
    /// Makes the dialog no taller than <paramref name="area"/>, the pages scrolling where they
    /// are taller than that leaves room for, and moves it up within it. On a short screen, or
    /// one scaled up, the dialog as tall as its tallest page would run off the bottom, Close and
    /// all.
    /// </summary>
    internal void FitWithin(Rectangle area)
    {
        var over = Height - area.Height;
        if (over > 0)
        {
            host.MaximumHeight = host.Height - over;
            PerformLayout();
        }

        // Not one placed off every screen on purpose, as the tests place theirs.
        if (area.IntersectsWith(Bounds))
        {
            Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        }
    }

    /// <summary>
    /// Whether the pages scroll, as they do once <see cref="FitWithin"/> has made the dialog
    /// shorter than they are.
    /// </summary>
    internal bool PagesScroll => host.VerticalScroll.Visible;

    // A page: its name as a heading, then its parts one under another. Hidden until picked.
    void AddPage(string title, char glyph, Control[] parts)
    {
        var page = Column(Padding.Empty);
        // Docked to the top rather than filling the host, so a page taller than the host
        // scrolls in it.
        page.Dock = DockStyle.Top;
        page.Visible = false;
        page.Controls.Add(
            new Label
            {
                Text = title,
                AutoSize = true,
                Font = headingFont,
                Margin = new(1, 0, 3, 12)
            });
        foreach (var part in parts)
        {
            page.Controls.Add(part);
        }

        pages.Add(page);
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

        // Each page from its top, rather than as far down as the last one was scrolled.
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        host?.AutoScrollPosition = Point.Empty;
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

    static TableLayoutPanel Column(Padding margin) =>
        new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = margin
        };

    // A part of a page: a heading, what it is for, then its controls.
    TableLayoutPanel Section(string heading, string description, params Control[] controls)
    {
        var section = Column(new(0, 0, 0, 15));
        section.Controls.Add(
            new Label
            {
                Text = heading,
                AutoSize = true,
                Font = sectionFont,
                Margin = new(3, 0, 3, 3)
            });
        section.Controls.Add(Description(description, new(3, 0, 3, 6)));
        foreach (var control in controls)
        {
            section.Controls.Add(control);
        }

        return section;
    }

    static Label Description(string text, Padding margin) =>
        new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new(textWidth, 0),
            Margin = margin
        };

    // Grey text under a check box, lined up with the box's text.
    static Label Note(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new(textWidth, 0),
            Margin = new(21, 0, 3, 9),
            ForeColor = SystemColors.GrayText
        };

    // A table row rather than a flow, so each control is centred on the row's middle and a text
    // box lines up with the taller buttons beside it.
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
        Section(
            "Default folder",
            "The folder new tabs open in, and new windows given no folder to show. Reset goes " +
            "back to your user folder.",
            Row(defaultDirectory, browseDefaultDirectory, resetDefaultDirectory)),
        Section(
            "Text editor",
            "Edit, on the right-click menu of a text file, opens it in this editor.",
            textEditor),
        Section(
            "Tabs",
            "Other apps opening folders in Lode leave a tab for each. Lode can close those you " +
            "haven't looked at for a while, once you have gone to another app. It never closes " +
            "the tab a window shows, and Reopen closed tab (Ctrl+Shift+T) brings the others back.",
            Row(closeUnusedTabs, unusedTabHours, unusedTabHoursUnit))
    ];

    Control[] BuildViewPage() =>
    [
        Section(
            "Theme",
            "Light or dark, or whichever Windows' apps are set to. Lode takes it on when it next " +
            "starts: choose Exit from its icon in the notification area, then open it again.",
            theme),
        showHidden,
        showSystem,
        Note("Windows, Program Files, other people's folders under Users, and the files Windows protects."),
        expandToOpenFolder,
        Note(
            "The tree opens out down to the folder the tab shows, and selects it, as File " +
            "Explorer's does only with Expand to open folder on, which it is not by default. Off, " +
            "the tree stays as you leave it, and selects the folder only where it already shows."),
        showRecent,
        Note(
            "Pinned folders are the ones pinned to Quick access in File Explorer. Recent folders " +
            "are the ones Explorer adds there because you use them often.")
    ];

    Control[] BuildActionsPage() =>
    [
        Description(
            "Show each action as a button on the bar under the address bar, in the right-click " +
            "menus, or both, and in each, on its own or nested under its parent. The menus only " +
            "offer what applies to what was clicked.",
            new(3, 0, 3, 9)),
        new ScrollingHost(BuildActionsTable())
    ];

    Control[] BuildCodePage() =>
    [
        Section(
            "Code folder",
            "The folder your git repositories are in. Folders in it get a Git menu (pull, switch " +
            "to default branch, push, discard, purge) and Update NuGets on their right-click menu. " +
            "Lode watches it while it runs, so Windows won't let a folder it is in be renamed or " +
            "moved until it is cleared here, or Lode exits.",
            Row(codeDirectory, browseCodeDirectory, clearCodeDirectory)),
        Section(
            "Git purge",
            "Git purge deletes every file and folder git doesn't track, ignored ones " +
            "included, such as bin and obj. It moves them to the Recycle Bin.",
            purgePermanently),
        Section(
            "Microsoft Defender",
            "Defender scans every file builds and git write in the code folder, which slows " +
            "them down. Excluding the folder stops that, as Rider suggests, and removes the " +
            "exclusions under it, which it makes redundant. Windows asks for administrator " +
            "permission.",
            excludeFromDefender)
    ];

    Control[] BuildSearchPage()
    {
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(turnOffWindowsSearch);
        buttons.Controls.Add(turnOnWindowsSearch);

        return
        [
            Section(
                "Search box",
                "The search box at the top right finds what is named like what is typed, in the " +
                "folder open and every folder beneath it. In a repository it leaves out what git " +
                "ignores, bin and node_modules say, as Find in Files does, and never goes into .git.",
                searchIgnored),
            Section(
                "Find in Files",
                "Ctrl+Shift+F searches what files hold, in the folder open, leaving out what git " +
                "ignores. With Context on in its strip, it shows lines either side of each match.",
                Row(contextLines, contextLinesUnit)),
            Section(
                "Windows Search",
                "Windows Search indexes your files in the background, for Start's search and " +
                "Explorer's. Lode doesn't use it: its search reads the folders itself. Turning it " +
                "off stops the indexing for good, but Start, Settings, Outlook and OneNote then " +
                "find no files, and Explorer's search becomes slower. Turn on starts it again as " +
                "Windows has it. Windows asks for administrator permission.",
                windowsSearchStatus,
                buttons)
        ];
    }

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
                "Revert gives the links back to that app. This applies to your Windows account only.",
                new(3, 0, 3, 3))
        };
        foreach (var handler in RepositoryLinks.All)
        {
            var status = new Label
            {
                Name = $"linkStatus.{handler.Id}",
                AutoSize = true,
                MaximumSize = new(textWidth, 0),
                Margin = new(3, 6, 3, 3)
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
            take.Click += (_, _) => ChangeLinks(() => repositoryLinks.Take(handler));
            revert.Click += (_, _) => ChangeLinks(() => repositoryLinks.GiveBack(handler));
            linkRows.Add((handler, status, take, revert));
            controls.Add(status);
            controls.Add(Row(take, revert));
        }

        return controls.ToArray();
    }

    void ChangeLinks(Action change)
    {
        try
        {
            change();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateLinkState();
    }

    void UpdateLinkState()
    {
        foreach (var (handler, status, take, revert) in linkRows)
        {
            var taken = repositoryLinks.IsTaken(handler);
            var here = repositoryLinks.IsTakenHere(handler);
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
            }
            else
            {
                opens = "No app opens them.";
            }

            // An app picked in Windows' own settings is asked for the links ahead of the
            // command Lode writes.
            if (taken &&
                repositoryLinks.ChosenInWindows(handler) is { } chosen)
            {
                opens += $" But Windows' Default apps has {chosen} chosen for {handler.Scheme} links, which comes first, so they still open there.";
            }

            status.Text = $"{handler.Buttons}: {opens}";
            take.Enabled = !here;
            revert.Enabled = taken;
        }
    }

    Control[] BuildExplorerPage()
    {
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(replaceButton);
        buttons.Controls.Add(restoreButton);

        return
        [
            Description(
                "Open folders, drives and Win+E in Lode instead of File Explorer. This applies to " +
                "your Windows account only. Explorer's own Open stays on every folder's " +
                "right-click menu, and the taskbar's File Explorer button still opens Explorer.",
                new(3, 0, 3, 9)),
            explorerStatus,
            buttons
        ];
    }

    Control[] BuildUpdatesPage()
    {
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(applyUpdate);
        buttons.Controls.Add(checkForUpdates);

        return
        [
            Description(
                "Lode asks NuGet for a newer version once a day, and shows one beside the free " +
                "space at the bottom of each window. Updating closes Lode, runs dotnet tool " +
                "update, and starts Lode again with the tabs it had.",
                new(3, 0, 3, 9)),
            updateStatus,
            buttons
        ];
    }

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
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
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

    // Where each action shows: a button on the actions bar, an entry in the right-click menus,
    // both or neither, and in each, whether nested under its parent. A tick changes every window
    // at once.
    TableLayoutPanel BuildActionsTable()
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 7,
            Margin = Padding.Empty
        };
        // Laid out once, when every row is in. The table sizes itself to what it holds, so each
        // control added would otherwise lay out all of it again, which for a couple of hundred
        // took most of the half second Options took to open.
        table.SuspendLayout();
        // Empty, and sized to it, so they leave the icon and name columns as narrow as their rows.
        table.Controls.Add(new Label {AutoSize = true}, 0, 0);
        table.Controls.Add(new Label {AutoSize = true}, 1, 0);
        // Over names, so to their left, as they are.
        var parentHeading = ColumnHeading("Parent");
        parentHeading.Anchor = AnchorStyles.Left;
        parentHeading.Margin = new(0, 0, 12, 3);
        table.Controls.Add(parentHeading, 2, 0);
        table.Controls.Add(ColumnHeading("Button"), 3, 0);
        table.Controls.Add(ColumnHeading("Nested"), 4, 0);
        table.Controls.Add(ColumnHeading("Right-click"), 5, 0);
        table.Controls.Add(ColumnHeading("Nested"), 6, 0);

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
            var button = new CheckBox
            {
                Name = $"button.{action.Id}",
                AccessibleName = $"{action.Text} as a button",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new(3, 1, 3, 1),
                Checked = settings.ShowsAsButton(action.Id)
            };
            var menu = new CheckBox
            {
                Name = $"menu.{action.Id}",
                AccessibleName = $"{action.Text} in the right-click menu",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new(3, 1, 3, 1),
                Checked = settings.ShowsInMenu(action.Id)
            };
            table.Controls.Add(icon, 0, row);
            table.Controls.Add(name, 1, row);
            table.Controls.Add(button, 3, row);
            table.Controls.Add(menu, 5, row);
            var id = action.Id;
            if (action.Parent is not { } parent)
            {
                EventHandler placed = (_, _) =>
                {
                    settings.SetPlacement(id, button.Checked, menu.Checked);
                    SettingsChanged?.Invoke(SettingsChange.Actions);
                };
                button.CheckedChanged += placed;
                menu.CheckedChanged += placed;
                row++;
                continue;
            }

            var buttonNested = NestedCheckBox($"buttonNested.{id}", $"{action.Text} under {parent.Text} on the bar", settings.NestsButton(id), button.Checked);
            var menuNested = NestedCheckBox($"menuNested.{id}", $"{action.Text} under {parent.Text} in the right-click menu", settings.NestsInMenu(id), menu.Checked);
            EventHandler changed = (_, _) =>
            {
                // Nesting only means anything where the action shows.
                buttonNested.Enabled = button.Checked;
                menuNested.Enabled = menu.Checked;
                settings.SetPlacement(id, button.Checked, menu.Checked, buttonNested.Checked, menuNested.Checked);
                SettingsChanged?.Invoke(SettingsChange.Actions);
            };
            button.CheckedChanged += changed;
            menu.CheckedChanged += changed;
            buttonNested.CheckedChanged += changed;
            menuNested.CheckedChanged += changed;
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
            table.Controls.Add(buttonNested, 4, row);
            table.Controls.Add(menuNested, 6, row);
            row++;
        }

        table.ResumeLayout();
        return table;
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
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
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

    // Holds every page, one shown at a time, and asks for room for the largest, so the dialog
    // keeps its size whichever page is shown.
    // Holds the actions table, and scrolls it once it runs past a height the other pages fit in,
    // so the list of actions doesn't set the dialog's height.
    sealed class ScrollingHost : Panel
    {
        const int maximumHeight = 360;

        Control content;

        public ScrollingHost(Control content)
        {
            this.content = content;
            AutoSize = true;
            AutoScroll = true;
            Margin = Padding.Empty;
            content.Location = Point.Empty;
            Controls.Add(content);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var preferred = content.GetPreferredSize(Size.Empty);
            var height = LogicalToDeviceUnits(maximumHeight);
            if (preferred.Height <= height)
            {
                return preferred;
            }

            // Room beside the table for the scroll bar, so it never covers the last column.
            return new(preferred.Width + SystemInformation.VerticalScrollBarWidth, height);
        }

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);
        }
    }

    sealed class PageHost : Panel
    {
        public PageHost() =>
            AutoScroll = true;

        /// <summary>
        /// How tall it may be, in pixels, the pages scrolling past that. 0 for as tall as the
        /// tallest page.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MaximumHeight
        {
            get;
            set
            {
                field = value;
                // What it asks for has changed, which the layout it is in only hears this way.
                Parent?.PerformLayout(this, nameof(MaximumHeight));
            }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var size = Size.Empty;
            foreach (Control page in Controls)
            {
                var preferred = page.GetPreferredSize(proposedSize);
                size = new(Math.Max(size.Width, preferred.Width), Math.Max(size.Height, preferred.Height));
            }

            if (MaximumHeight == 0 ||
                size.Height <= MaximumHeight)
            {
                return size;
            }

            // Room beside the pages for the scroll bar, so it never covers what is at their right.
            return new(size.Width + SystemInformation.VerticalScrollBarWidth, MaximumHeight);
        }
    }
}
