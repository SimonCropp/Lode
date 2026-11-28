/// <summary>
/// The find bar over a diff, as the filter over Changes' files is drawn, in GitHub Desktop's grey
/// block: what to find, Match case as Find in Files' Aa toggle, how many matches there are and
/// which is gone to, the buttons to the match before and the next, and the × that closes it.
/// Ctrl+F in the diff opens it; Enter and F3 go to the next match, Shift+Enter and Shift+F3 to
/// the one before, Alt+C matches case or not, and Escape closes it.
/// </summary>
sealed class DiffFindBar : Panel
{
    // Pixels at 100% scale.
    const int boxWidth = 260;

    FramedTextBox frame = new()
    {
        Desktop = true,
        Margin = new(0, 0, 4, 0)
    };

    FindToggle matchCase = new()
    {
        Text = "Aa",
        Name = "matchCase",
        AccessibleName = "Match case",
        Anchor = AnchorStyles.Left
    };

    GlyphButton previous = Button(Icons.ChevronUp, "previousMatch", "Previous match (Shift+F3)");
    GlyphButton next = Button(Icons.ChevronDown, "nextMatch", "Next match (F3)");
    GlyphButton close = Button(Icons.Close, "closeFind", "Close (Esc)");

    Label count = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new(6, 0, 0, 0),
        UseMnemonic = false,
        ForeColor = DesktopStyle.SecondaryText
    };

    ToolTip tip = new();

    /// <summary>
    /// What to find, or whether to match case, changed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The next match asked for, or, given false, the one before.
    /// </summary>
    public event Action<bool>? StepRequested;

    public event Action? CloseRequested;

    public DiffFindBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        BackColor = DesktopStyle.AltBackground;
        Padding = new(DesktopStyle.Inset, 5, 4, 6);
        Height = frame.Height + Padding.Vertical;
        frame.Width = LogicalToDeviceUnits(boxWidth);
        frame.Box.PlaceholderText = "Find in diff";
        frame.Box.AccessibleName = "Find in diff";
        frame.Box.TextChanged += (_, _) => Changed?.Invoke();
        matchCase.CheckedChanged += (_, _) => Changed?.Invoke();
        previous.Click += (_, _) => StepRequested?.Invoke(false);
        next.Click += (_, _) => StepRequested?.Invoke(true);
        close.Click += (_, _) => CloseRequested?.Invoke();
        tip.SetToolTip(matchCase, "Match case (Alt+C)");
        tip.SetToolTip(previous, previous.AccessibleDescription);
        tip.SetToolTip(next, next.AccessibleDescription);
        tip.SetToolTip(close, close.AccessibleDescription);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(frame, 0, 0);
        layout.Controls.Add(matchCase, 1, 0);
        layout.Controls.Add(previous, 2, 0);
        layout.Controls.Add(next, 3, 0);
        layout.Controls.Add(count, 4, 0);
        layout.Controls.Add(close, 6, 0);
        Controls.Add(layout);
    }

    static GlyphButton Button(char glyph, string name, string description) =>
        new()
        {
            Text = glyph.ToString(),
            Name = name,
            AccessibleName = description[..description.IndexOf(" (", StringComparison.Ordinal)],
            AccessibleDescription = description,
            Size = new(28, 24),
            Margin = new(1, 0, 1, 0),
            Anchor = AnchorStyles.Left,
            TabStop = false
        };

    /// <summary>
    /// The box what to find is typed in.
    /// </summary>
    public TextBox Box => frame.Box;

    public string FindText => frame.Box.Text;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MatchCase
    {
        get => matchCase.Checked;
        set => matchCase.Checked = value;
    }

    /// <summary>
    /// What it says of the matches: which is gone to of how many, or that there are none.
    /// </summary>
    public string CountText => count.Text;

    /// <summary>
    /// The box focused, what is in it selected to type over, as Ctrl+F leaves it.
    /// </summary>
    public void FocusBox()
    {
        frame.Box.Focus();
        frame.Box.SelectAll();
    }

    /// <summary>
    /// Says which match, from 0, is gone to of how many; -1 for none gone to.
    /// </summary>
    public void ShowCount(int current, int total)
    {
        if (FindText.Length == 0)
        {
            count.Text = "";
        }
        else if (total == 0)
        {
            count.Text = "No results";
        }
        else if (current < 0)
        {
            count.Text = $"{total:N0} found";
        }
        else
        {
            count.Text = $"{current + 1:N0} of {total:N0}";
        }

        previous.Enabled = total > 0;
        next.Enabled = total > 0;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter:
            case Keys.Shift | Keys.Enter:
                StepRequested?.Invoke(keyData == Keys.Enter);
                return true;
            case Keys.Alt | Keys.C:
                matchCase.Checked = !matchCase.Checked;
                return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        using var pen = new Pen(DesktopStyle.Border);
        args.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tip.Dispose();
        }

        base.Dispose(disposing);
    }
}
