/// <summary>
/// A window's address bar, Explorer's address row along its top: Back, Forward, Up and Refresh,
/// the folder as a breadcrumb, which takes a path typed, the search box, New tab, and the ⋯ menu
/// of Lode's own commands. Each acts on the window's active view.
/// </summary>
sealed class AddressBar : IDisposable
{
    // Glyphs of the icon font (see Icons) for the row's buttons.
    const string backGlyph = "";
    const string forwardGlyph = "";
    const string upGlyph = "";
    const string refreshGlyph = "";
    const string moreGlyph = "";
    const string addGlyph = "";

    // How many places Back's and Forward's lists show, the nearest: as many as are easily read.
    const int mostHistory = 15;

    Form window;
    Func<FolderView?> activeView;
    Func<IReadOnlyList<QuickMenu.Command?>> menuCommands;
    ToolTip toolTip = new();
    Breadcrumb breadcrumb;
    TextBox addressBox;
    FramedTextBox filterFrame = new();
    TextBox filterBox;
    Button backButton;
    Button forwardButton;
    Button upButton;
    bool updatingFilter;

    /// <param name="window">The window the bar is along the top of.</param>
    /// <param name="settings">Whether the folders a chevron lists include hidden ones, and
    /// Windows' own.</param>
    /// <param name="activeView">The view the bar shows and acts on, null while there is none.</param>
    /// <param name="reload">Refresh.</param>
    /// <param name="newTab">New tab.</param>
    /// <param name="openInNewTab">A folder of the address opened in a new tab: one middle-clicked
    /// or Ctrl+clicked, or Open in new tab on its right-click menu.</param>
    /// <param name="menuCommands">What the ⋯ menu lists, as it opens. A null entry is a separator.</param>
    public AddressBar(Form window, Settings settings, Func<FolderView?> activeView, Action reload, Action newTab, Action<string> openInNewTab, Func<IReadOnlyList<QuickMenu.Command?>> menuCommands)
    {
        this.window = window;
        this.activeView = activeView;
        this.menuCommands = menuCommands;
        breadcrumb = new(settings);
        addressBox = breadcrumb.Box;
        filterBox = filterFrame.Box;

        backButton = CreateButton(backGlyph, "Back (Alt+Left)", () => activeView()?.GoBack());
        forwardButton = CreateButton(forwardGlyph, "Forward (Alt+Right)", () => activeView()?.GoForward());

        // Right-clicked, each lists where it would go, to go several steps at once.
        backButton.MouseUp += (_, args) => ShowHistory(backButton, args, _ => _.BackHistory);
        forwardButton.MouseUp += (_, args) => ShowHistory(forwardButton, args, _ => _.ForwardHistory);
        upButton = CreateButton(upGlyph, "Up (Alt+Up)", () => activeView()?.GoUp());
        var refreshButton = CreateButton(refreshGlyph, "Refresh (F5)", reload);
        var newTabButton = CreateButton(addGlyph, "New tab (Ctrl+T)", newTab);
        var menuButton = CreateButton(moreGlyph, "Menu", () => { });
        menuButton.Click += (_, _) => ShowMenu(menuButton);

        addressBox.Name = "address";
        addressBox.AccessibleName = "Address";
        breadcrumb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        breadcrumb.Margin = new(6, 0, 4, 0);
        breadcrumb.PathClicked += _ =>
        {
            activeView()?.Navigate(_);
            activeView()?.FocusList();
        };
        breadcrumb.NewTabRequested += openInNewTab;
        addressBox.AutoCompleteMode = AutoCompleteMode.Suggest;
        addressBox.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
        addressBox.KeyDown += OnAddressKeyDown;
        addressBox.Enter += (_, _) => window.BeginInvoke(addressBox.SelectAll);

        filterBox.Name = "filter";
        filterBox.AccessibleName = "Search";
        filterFrame.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        filterFrame.Margin = new(4, 0, 4, 0);
        filterBox.PlaceholderText = "Search this folder (Ctrl+F)";
        filterFrame.Glyph = Icons.Search;
        filterBox.TextChanged += OnFilterTextChanged;
        filterBox.KeyDown += OnFilterKeyDown;

        Row = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new(4, 3, 4, 3),
            // Explorer's address row, a shade off white, with the command bar white below it.
            BackColor = ExplorerStyle.AddressRow
        };
        Row.ColumnStyles.Add(new(SizeType.AutoSize));
        Row.ColumnStyles.Add(new(SizeType.AutoSize));
        Row.ColumnStyles.Add(new(SizeType.AutoSize));
        Row.ColumnStyles.Add(new(SizeType.AutoSize));
        Row.ColumnStyles.Add(new(SizeType.Percent, 100));
        Row.ColumnStyles.Add(new(SizeType.Absolute, 240));
        Row.ColumnStyles.Add(new(SizeType.AutoSize));
        Row.ColumnStyles.Add(new(SizeType.AutoSize));
        Row.Controls.Add(backButton, 0, 0);
        Row.Controls.Add(forwardButton, 1, 0);
        Row.Controls.Add(upButton, 2, 0);
        Row.Controls.Add(refreshButton, 3, 0);
        Row.Controls.Add(breadcrumb, 4, 0);
        Row.Controls.Add(filterFrame, 5, 0);
        Row.Controls.Add(newTabButton, 6, 0);
        Row.Controls.Add(menuButton, 7, 0);
    }

    /// <summary>
    /// The row the bar is, for the window to dock along its top.
    /// </summary>
    public TableLayoutPanel Row { get; }

    /// <summary>
    /// Brings the bar in line with the tab showing: its folder in the address, its filter in the
    /// search box, and whether it can go back, forward and up. Find Results and the Diff tab,
    /// <paramref name="view"/> null, have no folder: going back, forward and up, the address and
    /// the search box are for the folders' tabs.
    /// </summary>
    public void Update(FolderView? view)
    {
        breadcrumb.Enabled = view != null;
        filterFrame.Enabled = view != null;
        if (view == null)
        {
            backButton.Enabled = false;
            forwardButton.Enabled = false;
            upButton.Enabled = false;
            return;
        }

        breadcrumb.Path = view.CurrentPath;
        updatingFilter = true;
        filterBox.Text = view.Filter;
        updatingFilter = false;
        backButton.Enabled = view.CanGoBack;
        forwardButton.Enabled = view.CanGoForward;
        upButton.Enabled = view.CanGoUp;
    }

    /// <summary>
    /// Ctrl+L: the address, as a path to edit.
    /// </summary>
    public void FocusAddress() =>
        breadcrumb.BeginEdit();

    /// <summary>
    /// Ctrl+F: the search box, with what it says selected.
    /// </summary>
    public void FocusFilter()
    {
        filterBox.Focus();
        filterBox.SelectAll();
    }

    void ShowHistory(Button button, MouseEventArgs args, Func<FolderView, IReadOnlyList<(string Path, int Position)>> history)
    {
        if (args.Button != MouseButtons.Right ||
            activeView() is not { } view ||
            HistoryCommands(view, history(view)) is not { Count: > 0 } commands)
        {
            return;
        }

        var menu = new ExplorerMenu();
        foreach (var (text, go) in commands)
        {
            menu.Items.Add(text, null, (_, _) => go());
        }

        menu.Closed += (_, _) => window.BeginInvoke(menu.Dispose);
        menu.Show(button, new(0, button.Height));
    }

    /// <summary>
    /// What Back's or Forward's list offers: each place by its name, the nearest first, and
    /// going straight to it.
    /// </summary>
    internal static List<(string Text, Action Go)> HistoryCommands(FolderView view, IReadOnlyList<(string Path, int Position)> history) =>
        history
            .Take(mostHistory)
            .Select(_ => (Locations.DisplayName(_.Path), (Action)(() => view.GoToHistory(_.Position))))
            .ToList();

    // Its text is a glyph of the icon font, which a screen reader can't say, so it is named by
    // its tooltip, less the keys, which describe it.
    Button CreateButton(string glyph, string tip, Action action)
    {
        var name = tip;
        var keys = tip.IndexOf(" (", StringComparison.Ordinal);
        if (keys > 0)
        {
            name = tip[..keys];
        }

        var button = new GlyphButton
        {
            Text = glyph,
            AccessibleName = name,
            AccessibleDescription = tip,
            Size = new(36, 30),
            Margin = new(1),
            TabStop = false
        };
        button.Click += (_, _) => action();
        toolTip.SetToolTip(button, tip);
        return button;
    }

    // The ⋯ menu at the right of the bar: Lode's own commands, apart from the actions, which are
    // buttons or under See more.
    void ShowMenu(Control button)
    {
        Action? chosen = null;
        var strip = QuickMenu.Build(menuCommands(), null, _ => chosen = _);
        // Run the choice once the menu has closed, so a dialog it opens gets the focus.
        strip.Closed += (_, _) => strip.BeginInvoke(
            () =>
            {
                strip.Dispose();
                chosen?.Invoke();
            });
        strip.Show(button, new(0, button.Height));
    }

    void OnAddressKeyDown(object? sender, KeyEventArgs args)
    {
        var view = activeView();
        if (view == null)
        {
            return;
        }

        if (args.KeyCode == Keys.Enter)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            view.GoToTyped(addressBox.Text);
            breadcrumb.Path = view.CurrentPath;
            view.FocusList();
        }
        else if (args.KeyCode == Keys.Escape)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            breadcrumb.Path = view.CurrentPath;
            view.FocusList();
        }
    }

    void OnFilterTextChanged(object? sender, EventArgs args)
    {
        if (!updatingFilter &&
            activeView() is { } view)
        {
            view.Filter = filterBox.Text;
        }
    }

    void OnFilterKeyDown(object? sender, KeyEventArgs args)
    {
        // What it says, searched for inside the files instead.
        if (args.KeyData == (Keys.Control | Keys.Enter))
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            if (activeView() is { CanSearchInsideTheFiles: true } view)
            {
                view.SearchInsideTheFiles();
            }

            return;
        }

        if (args.KeyCode == Keys.Escape)
        {
            filterBox.Clear();
        }
        else if (args.KeyCode is not (Keys.Enter or Keys.Down))
        {
            return;
        }

        args.Handled = true;
        args.SuppressKeyPress = true;
        activeView()?.FocusList();
    }

    public void Dispose() =>
        toolTip.Dispose();
}
