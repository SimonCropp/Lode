/// <summary>
/// Find in Files' strip, along the bottom of the window as Sublime's find panel is: what to
/// find, where, and what to replace it with, the toggles for how, and Find and Replace. Ctrl+Shift+F
/// opens it on the folder open; Esc stops a search running, and otherwise closes it. The
/// toggles keep Sublime's keys: Alt+R regex, Alt+C case, Alt+W whole word, Alt+A preserve case,
/// and Alt+X context. Up and Down in a box go back through what was typed there before.
/// </summary>
sealed class FindPanel : Panel
{
    readonly Settings settings;
    readonly ToolTip toolTip = new();

    readonly FramedTextBox findFrame = new();
    readonly FramedTextBox whereFrame = new();
    readonly FramedTextBox replaceFrame = new();

    readonly FindToggle regex = Toggle(".*", "regex");
    readonly FindToggle caseSensitive = Toggle("Aa", "caseSensitive");
    readonly FindToggle wholeWord = Toggle("ab", "wholeWord");
    readonly FindToggle preserveCase = Toggle("AB", "preserveCase");
    readonly FindToggle context = Toggle("Context", "context");
    readonly FindToggle hidden = Toggle("Hidden", "hidden");
    readonly FindToggle ignored = Toggle("Git-ignored", "ignored");

    readonly Button findButton = TextButton("Find", "find");
    readonly Button replaceButton = TextButton("Replace", "replace");
    readonly Button whereButton = TextButton("…", "whereMenu");

    // Where each box is in its history, while Up and Down go through it: -1 for what was typed.
    readonly Dictionary<TextBox, (int Index, string Typed)> going = [];
    bool recalling;

    public TextBox FindBox => findFrame.Box;
    public TextBox WhereBox => whereFrame.Box;
    public TextBox ReplaceBox => replaceFrame.Box;

    /// <summary>
    /// Find was asked for: the button, or Enter.
    /// </summary>
    public event Action? FindRequested;

    /// <summary>
    /// Replace was asked for: the button, or Ctrl+Shift+Enter.
    /// </summary>
    public event Action? ReplaceRequested;

    /// <summary>
    /// Esc, with a search running, to stop it.
    /// </summary>
    public event Action? StopRequested;

    /// <summary>
    /// Whether a search is running, for Esc to stop it rather than close the strip.
    /// </summary>
    public Func<bool> Searching = () => false;

    /// <summary>
    /// The folder the tab showing has open, for "Add current folder". Null where there is none.
    /// </summary>
    public Func<string?> CurrentFolder = () => null;

    /// <summary>
    /// Asks for a folder to add to Where. Replaced in tests, which can't answer a dialog.
    /// </summary>
    internal Func<string?> BrowseForFolder;

    public FindPanel(Settings settings)
    {
        this.settings = settings;
        BrowseForFolder = ShowFolderDialog;
        Dock = DockStyle.Bottom;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = ExplorerStyle.AddressRow;
        Padding = new(6, 4, 6, 4);
        Visible = false;

        regex.Checked = settings.FindRegex;
        caseSensitive.Checked = settings.FindCaseSensitive;
        wholeWord.Checked = settings.FindWholeWord;
        preserveCase.Checked = settings.FindPreserveCase;
        context.Checked = settings.FindContext;
        hidden.Checked = settings.FindHidden;
        ignored.Checked = settings.FindIgnored;
        regex.CheckedChanged += (_, _) => settings.FindRegex = regex.Checked;
        caseSensitive.CheckedChanged += (_, _) => settings.FindCaseSensitive = caseSensitive.Checked;
        wholeWord.CheckedChanged += (_, _) => settings.FindWholeWord = wholeWord.Checked;
        preserveCase.CheckedChanged += (_, _) => settings.FindPreserveCase = preserveCase.Checked;
        context.CheckedChanged += (_, _) => settings.FindContext = context.Checked;
        hidden.CheckedChanged += (_, _) => settings.FindHidden = hidden.Checked;
        ignored.CheckedChanged += (_, _) => settings.FindIgnored = ignored.Checked;
        toolTip.SetToolTip(regex, "Regular expression (Alt+R)");
        toolTip.SetToolTip(caseSensitive, "Match case (Alt+C)");
        toolTip.SetToolTip(wholeWord, "Whole word (Alt+W)");
        toolTip.SetToolTip(preserveCase, "Preserve case when replacing (Alt+A)");
        toolTip.SetToolTip(context, "Show the lines either side of each match (Alt+X)");
        toolTip.SetToolTip(hidden, "Search hidden files and folders");
        toolTip.SetToolTip(ignored, "Search what git ignores too, such as bin and node_modules");
        toolTip.SetToolTip(whereButton, "Add a folder or a filter");
        whereButton.AccessibleName = "Add a folder or a filter";

        Box(findFrame, "findText", "Find", "What to find");
        Box(whereFrame, "whereText", "Where", "Folders, then *.cs to include and -*.min.js to exclude");
        Box(replaceFrame, "replaceText", "Replace", "What to replace it with");
        findButton.Click += (_, _) => FindRequested?.Invoke();
        replaceButton.Click += (_, _) => ReplaceRequested?.Invoke();
        whereButton.Click += (_, _) => ShowWhereMenu();

        var close = new GlyphButton
        {
            Name = "closeFind",
            AccessibleName = "Close",
            AccessibleDescription = "Close (Esc)",
            Text = "",
            Size = new(30, 26),
            Margin = new(1),
            TabStop = false
        };
        close.Click += (_, _) => Close();
        toolTip.SetToolTip(close, "Close (Esc)");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 3,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(Toggles(regex, caseSensitive, wholeWord, preserveCase), 0, 0);
        layout.Controls.Add(Toggles(context, hidden, ignored), 0, 1);
        layout.Controls.Add(Label("Find:"), 1, 0);
        layout.Controls.Add(Label("Where:"), 1, 1);
        layout.Controls.Add(Label("Replace:"), 1, 2);
        layout.Controls.Add(findFrame, 2, 0);
        layout.Controls.Add(whereFrame, 2, 1);
        layout.Controls.Add(replaceFrame, 2, 2);
        layout.Controls.Add(findButton, 3, 0);
        layout.Controls.Add(whereButton, 3, 1);
        layout.Controls.Add(replaceButton, 3, 2);
        layout.Controls.Add(close, 4, 0);
        Controls.Add(layout);
    }

    static FindToggle Toggle(string text, string name) =>
        new()
        {
            Text = text,
            Name = name,
            AccessibleName = name
        };

    static Button TextButton(string text, string name) =>
        new()
        {
            Text = text,
            Name = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new(4, 2, 2, 2),
            Padding = new(6, 0, 6, 0),
            UseVisualStyleBackColor = true
        };

    static Label Label(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new(8, 0, 4, 0)
        };

    static FlowLayoutPanel Toggles(params Control[] toggles)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new(0, 1, 0, 1),
            Anchor = AnchorStyles.Left
        };
        flow.Controls.AddRange(toggles);
        return flow;
    }

    void Box(FramedTextBox frame, string name, string accessibleName, string placeholder)
    {
        frame.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        frame.Margin = new(0, 2, 0, 2);
        frame.Box.Name = name;
        frame.Box.AccessibleName = accessibleName;
        frame.Box.PlaceholderText = placeholder;
        frame.Box.KeyDown += (_, args) => OnBoxKeyDown(frame.Box, args);
        frame.Box.TextChanged += (_, _) =>
        {
            if (!recalling)
            {
                going.Remove(frame.Box);
            }
        };
    }

    /// <summary>
    /// What the strip says to find, and how.
    /// </summary>
    public FindQuery ReadQuery()
    {
        var lines = 0;
        if (context.Checked)
        {
            lines = Math.Max(0, settings.FindContextLines);
        }

        return new(
            FindBox.Text,
            WhereBox.Text,
            regex.Checked,
            caseSensitive.Checked,
            wholeWord.Checked,
            lines,
            hidden.Checked,
            ignored.Checked);
    }

    public bool PreserveCase => preserveCase.Checked;

    /// <summary>
    /// Shows the strip, with <paramref name="folder"/> in Where in place of the folders there,
    /// and what to find selected, to type over.
    /// </summary>
    public void Open(string? folder)
    {
        if (folder != null)
        {
            WhereBox.Text = SearchWhere.WithFolder(WhereBox.Text, folder);
        }

        Visible = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    /// <summary>
    /// Puts <paramref name="find"/> in Find, as text or a regular expression: from the search
    /// box, whose filter matched no names.
    /// </summary>
    public void SetFind(string find, bool isRegex)
    {
        FindBox.Text = find;
        regex.Checked = isRegex;
    }

    public void Close()
    {
        Visible = false;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// The strip closed, for the window to put the focus back where it was.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Keeps what was typed in each box, for Up and Down to go back to.
    /// </summary>
    public void Remember(bool replacing)
    {
        Settings.Remember(settings.FindHistory, FindBox.Text);
        Settings.Remember(settings.WhereHistory, WhereBox.Text);
        if (replacing)
        {
            Settings.Remember(settings.ReplaceHistory, ReplaceBox.Text);
        }
    }

    List<string> HistoryOf(TextBox box)
    {
        if (box == WhereBox)
        {
            return settings.WhereHistory;
        }

        if (box == ReplaceBox)
        {
            return settings.ReplaceHistory;
        }

        return settings.FindHistory;
    }

    void OnBoxKeyDown(TextBox box, KeyEventArgs args)
    {
        switch (args.KeyData)
        {
            case Keys.Enter:
                FindRequested?.Invoke();
                break;
            case Keys.Control | Keys.Shift | Keys.Enter:
                ReplaceRequested?.Invoke();
                break;
            case Keys.Up:
                Recall(box, 1);
                break;
            case Keys.Down:
                Recall(box, -1);
                break;
            default:
                return;
        }

        args.Handled = true;
        args.SuppressKeyPress = true;
    }

    // Goes a step back through the box's history, or forward, ending at what was typed.
    void Recall(TextBox box, int step)
    {
        var history = HistoryOf(box);
        var (index, typed) = going.GetValueOrDefault(box, (-1, box.Text));
        var next = Math.Clamp(index + step, -1, history.Count - 1);
        if (next == index)
        {
            return;
        }

        recalling = true;
        if (next < 0)
        {
            box.Text = typed;
        }
        else
        {
            box.Text = history[next];
        }

        recalling = false;
        going[box] = (next, typed);
        box.SelectionStart = box.TextLength;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        var toggle = keyData switch
        {
            Keys.Alt | Keys.R => regex,
            Keys.Alt | Keys.C => caseSensitive,
            Keys.Alt | Keys.W => wholeWord,
            Keys.Alt | Keys.A => preserveCase,
            Keys.Alt | Keys.X => context,
            _ => null
        };
        if (toggle != null)
        {
            toggle.Checked = !toggle.Checked;
            return true;
        }

        if (keyData == Keys.Escape)
        {
            if (Searching())
            {
                StopRequested?.Invoke();
            }
            else
            {
                Close();
            }

            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    void ShowWhereMenu()
    {
        var menu = new ExplorerMenu();
        menu.Items.Add("Add folder…", null, (_, _) => AddFolderBrowsed());
        if (CurrentFolder() is { } folder)
        {
            menu.Items.Add("Add current folder", null, (_, _) => AddToWhere(folder));
        }

        menu.Items.Add("Add open folders", null, (_, _) => AddToWhere(SearchWhere.OpenFolders));
        menu.Items.Add("Add include filter", null, (_, _) => AddToWhere("*.", selectFrom: 2));
        menu.Items.Add("Add exclude filter", null, (_, _) => AddToWhere("-*.", selectFrom: 3));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(whereButton, new(0, whereButton.Height));
    }

    void AddFolderBrowsed()
    {
        if (BrowseForFolder() is { } folder)
        {
            AddToWhere(folder);
        }
    }

    // Adds an entry at the end of Where, with the caret after it, or, for a filter whose
    // extension is to be typed, where that goes.
    internal void AddToWhere(string entry, int? selectFrom = null)
    {
        var text = WhereBox.Text.TrimEnd().TrimEnd(',');
        if (text.Length > 0)
        {
            text += ", ";
        }

        var start = text.Length;
        WhereBox.Text = text + entry;
        WhereBox.Focus();
        WhereBox.SelectionStart = start + (selectFrom ?? entry.Length);
    }

    string? ShowFolderDialog()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to search",
            UseDescriptionForTitle = true
        };
        if (CurrentFolder() is { } folder)
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return null;
        }

        return dialog.SelectedPath;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
