/// <summary>
/// The status bar's branch drop-up, as GitHub Desktop's Current branch list is: a box to filter
/// the branches by, with New branch beside it, over the branches, grouped as the default branch,
/// those switched to recently, and the rest. Enter, or a click, switches to the highlighted one;
/// Escape, or clicking elsewhere, closes it.
/// </summary>
sealed class BranchPicker : Form
{
    /// <summary>
    /// A line of the list: a group's heading, a branch, or, with no branch to show, why not.
    /// </summary>
    internal sealed record Row(string Text, GitBranches.Branch? Branch = null, bool Heading = false);

    // Drawn as the Clone dialog's boxes are, lined up with New branch beside it.
    FramedTextBox filterFrame = new()
    {
        Outlined = true,
        BesideButtons = true
    };
    Button newButton = new();
    ListBox list = new();
    GitBranches.Listing? listing;
    List<Row> shown = [];
    Font? headingFont;
    bool closing;

    // On screen, what opened it: the status bar's branch. It opens above that, as a drop-down on
    // a bar at the bottom of a window does. Empty, it is left where it was put.
    Rectangle opener;

    /// <summary>
    /// How long ago each branch was committed to is said against this. Tests put in a clock of
    /// their own.
    /// </summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// What has been typed to filter by. For tests.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Search
    {
        get => filterFrame.Box.Text;
        set => filterFrame.Box.Text = value;
    }

    /// <summary>
    /// The lines shown. For tests.
    /// </summary>
    internal IReadOnlyList<Row> Rows => shown;

    /// <summary>
    /// The line highlighted, or -1. For tests.
    /// </summary>
    internal int SelectedRow => list.SelectedIndex;

    /// <summary>
    /// The branch picked, once it has closed. Null when it was dismissed, or the branch checked
    /// out already was picked.
    /// </summary>
    public GitBranches.Branch? Chosen { get; private set; }

    /// <summary>
    /// Set when a new branch was asked for, once it has closed: the name to start from, made of
    /// what was typed, which may be nothing.
    /// </summary>
    public string? NewBranchName { get; private set; }

    /// <summary>
    /// The right-click entries for a branch, as GitHub Desktop has them: rename, copy its name,
    /// and so on. None without.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<GitBranches.Listing, GitBranches.Branch, IReadOnlyList<QuickMenu.Command?>>? MenuFor { get; init; }

    /// <summary>
    /// The right-click entry picked, to run once it has closed, so a dialog it opens has the
    /// window to itself.
    /// </summary>
    public Action? FollowUp { get; private set; }

    public BranchPicker(Rectangle opener = default)
    {
        this.opener = opener;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false;
        Text = "";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(420, 420);
        Padding = new(6);

        // As tall as New branch, so its frame and the button's line up.
        filterFrame.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
        filterFrame.Margin = new(0, 0, 6, 0);
        filterFrame.Box.AccessibleName = "Filter branches";
        filterFrame.Box.PlaceholderText = "Filter";
        filterFrame.Box.TextChanged += (_, _) => Refill();

        newButton.Text = "New branch";
        newButton.AutoSize = true;
        newButton.Margin = Padding.Empty;
        newButton.Click += (_, _) => AskForNewBranch();

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1
        };
        top.ColumnStyles.Add(new(SizeType.Percent, 100));
        top.ColumnStyles.Add(new(SizeType.AutoSize));
        top.Controls.Add(filterFrame, 0, 0);
        top.Controls.Add(newButton, 1, 0);

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.IntegralHeight = false;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.AccessibleName = "Branches";
        list.DrawItem += DrawRow;

        // Its scrollbar drawn as the folder's and tree's are: a thin thumb, which widens and
        // shows its arrows under the mouse.
        list.HandleCreated += (_, _) => SetWindowTheme(list.Handle, ExplorerStyle.ControlTheme, null);

        // A heading clicked on is no branch to highlight: the branch under it is.
        list.SelectedIndexChanged += (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index >= 0 &&
                shown[index].Branch == null)
            {
                list.SelectedIndex = Selectable(index, 1);
            }
        };
        list.MouseClick += (_, args) =>
        {
            var index = list.IndexFromPoint(args.Location);
            if (index >= 0 &&
                shown[index].Branch is { } branch)
            {
                Choose(branch);
            }
        };
        list.MouseUp += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
            {
                ShowMenu(list.IndexFromPoint(args.Location), args.Location);
            }
        };

        var gap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 6
        };
        Controls.Add(list);
        Controls.Add(gap);
        Controls.Add(top);
        Refill();
    }

    /// <summary>
    /// Shows the branches above <paramref name="opener"/>, finding them while it is open, and
    /// once it has closed switches to the one picked, or asks for a new branch.
    /// </summary>
    public static BranchPicker Show(
        Form owner,
        Rectangle opener,
        Task<GitBranches.Listing> branches,
        Action<GitBranches.Branch> switchTo,
        Action<string> newBranch,
        Func<GitBranches.Listing, GitBranches.Branch, IReadOnlyList<QuickMenu.Command?>>? menuFor = null)
    {
        var picker = new BranchPicker(opener)
        {
            MenuFor = menuFor
        };
        picker.FormClosed += (_, _) =>
        {
            var chosen = picker.Chosen;
            var name = picker.NewBranchName;
            var followUp = picker.FollowUp;
            owner.BeginInvoke(
                () =>
                {
                    picker.Dispose();
                    if (chosen != null)
                    {
                        switchTo(chosen);
                        return;
                    }

                    if (name != null)
                    {
                        newBranch(name);
                        return;
                    }

                    followUp?.Invoke();
                });
        };
        picker.Show(owner);
        _ = picker.Fill(branches);
        return picker;
    }

    /// <summary>
    /// Shows the branches once they are found. Until then it says it is finding them.
    /// </summary>
    internal async Task Fill(Task<GitBranches.Listing> branches)
    {
        var found = await branches;
        if (IsDisposed)
        {
            return;
        }

        listing = found;
        Refill();
    }

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        list.ItemHeight = Math.Max(list.Font.Height + LogicalToDeviceUnits(8), list.ItemHeight);
        headingFont = new(list.Font, FontStyle.Bold);

        // Placed once scaled for the screen it is on, so its bottom is where the branch's top is.
        if (!opener.IsEmpty)
        {
            Location = Place(opener, Size, Screen.FromRectangle(opener).WorkingArea);
        }
    }

    /// <summary>
    /// Where a drop-up of <paramref name="size"/> goes: its bottom left at the top left of what
    /// opened it, kept on the screen's <paramref name="area"/>. Below, where there is no room
    /// above.
    /// </summary>
    internal static Point Place(Rectangle opener, Size size, Rectangle area)
    {
        var x = Math.Clamp(opener.Left, area.Left, Math.Max(area.Left, area.Right - size.Width));
        var y = opener.Top - size.Height;
        if (y < area.Top)
        {
            y = Math.Max(area.Top, Math.Min(opener.Bottom, area.Bottom - size.Height));
        }

        return new(x, y);
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        filterFrame.Box.Focus();
    }

    protected override void OnDeactivate(EventArgs args)
    {
        base.OnDeactivate(args);
        CloseOnce();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headingFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (Press(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>
    /// Acts on a key pressed anywhere in it, giving whether it did. Enter on New branch is left
    /// to that button.
    /// </summary>
    internal bool Press(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                CloseOnce();
                return true;
            case Keys.Enter:
                if (newButton.Focused)
                {
                    return false;
                }

                Accept();
                return true;
            case Keys.Down:
                MoveSelection(1);
                return true;
            case Keys.Up:
                MoveSelection(-1);
                return true;
            case Keys.PageDown:
                MoveSelection(VisibleRows);
                return true;
            case Keys.PageUp:
                MoveSelection(-VisibleRows);
                return true;
            case Keys.Apps:
            case Keys.Shift | Keys.F10:
                if (list.SelectedIndex >= 0)
                {
                    var row = list.GetItemRectangle(list.SelectedIndex);
                    ShowMenu(list.SelectedIndex, new(row.Left + LogicalToDeviceUnits(24), row.Bottom));
                }

                return true;
        }

        return false;
    }

    /// <summary>
    /// The right-click entries for the branch at <paramref name="index"/>, each closing the list
    /// then running. None for a heading.
    /// </summary>
    internal List<QuickMenu.Command?> MenuAt(int index)
    {
        if (MenuFor == null ||
            listing == null ||
            index < 0 ||
            shown[index].Branch is not { } branch)
        {
            return [];
        }

        return MenuFor(listing, branch)
            .Select(Later)
            .ToList();

        QuickMenu.Command? Later(QuickMenu.Command? command)
        {
            if (command == null)
            {
                return null;
            }

            return command with
            {
                Action = () =>
                {
                    FollowUp = command.Action;
                    CloseOnce();
                }
            };
        }
    }

    // Highlights the branch right-clicked, as Explorer does, and shows its entries there. What
    // is picked runs once the menu has closed, as QuickMenu does.
    void ShowMenu(int index, Point location)
    {
        var commands = MenuAt(index);
        if (commands.Count == 0)
        {
            return;
        }

        list.SelectedIndex = index;
        Action? chosen = null;
        var strip = QuickMenu.Build(commands, null, _ => chosen = _);
        strip.Closed += (_, _) => strip.BeginInvoke(
            () =>
            {
                strip.Dispose();
                chosen?.Invoke();
            });
        strip.Show(list, location);
    }

    int VisibleRows => Math.Max(1, list.ClientSize.Height / list.ItemHeight);

    // The highlighted branch, or, with no branch shown but something typed, a new branch of
    // that name, as GitHub Desktop has it.
    void Accept()
    {
        if (list.SelectedIndex >= 0 &&
            shown[list.SelectedIndex].Branch is { } branch)
        {
            Choose(branch);
            return;
        }

        if (listing != null &&
            !shown.Any(_ => _.Branch != null) &&
            filterFrame.Box.Text.Trim().Length > 0)
        {
            AskForNewBranch();
        }
    }

    void MoveSelection(int offset)
    {
        if (!shown.Any(_ => _.Branch != null))
        {
            return;
        }

        var target = Math.Clamp(list.SelectedIndex + offset, 0, shown.Count - 1);
        list.SelectedIndex = Selectable(target, Math.Sign(offset));
    }

    // The branch at index, or the nearest one past it going step's way, or, with none that way,
    // the nearest the other way. -1 with no branch shown.
    int Selectable(int index, int step)
    {
        if (step == 0)
        {
            step = 1;
        }

        for (var current = index; current >= 0 && current < shown.Count; current += step)
        {
            if (shown[current].Branch != null)
            {
                return current;
            }
        }

        for (var current = index; current >= 0 && current < shown.Count; current -= step)
        {
            if (shown[current].Branch != null)
            {
                return current;
            }
        }

        return -1;
    }

    // The branch checked out already is somewhere to stay, so picking it only closes.
    void Choose(GitBranches.Branch branch)
    {
        if (!IsCurrent(branch))
        {
            Chosen = branch;
        }

        CloseOnce();
    }

    bool IsCurrent(GitBranches.Branch branch) =>
        !branch.Remote &&
        branch.Name == listing?.Current;

    void AskForNewBranch()
    {
        NewBranchName = SuggestedName(filterFrame.Box.Text);
        CloseOnce();
    }

    /// <summary>
    /// What was typed made into a branch's name, as GitHub Desktop does: words joined by dashes,
    /// as a branch's name can't have spaces, nor anything else git refuses in one.
    /// </summary>
    internal static string SuggestedName(string typed) =>
        RefName.Sanitized(typed.Trim());

    // Deactivating while closing would close it again.
    void CloseOnce()
    {
        if (closing)
        {
            return;
        }

        closing = true;
        Close();
    }

    void Refill()
    {
        shown = RowsFor(listing, filterFrame.Box.Text);
        using (list.SuspendPainting())
        {
            list.Items.Clear();
            foreach (var row in shown)
            {
                list.Items.Add(row.Text);
            }

            list.SelectedIndex = FirstSelected();
        }
    }

    // With nothing typed, the branch checked out, as GitHub Desktop has it; otherwise the best
    // match, which is first.
    int FirstSelected()
    {
        if (filterFrame.Box.Text.Trim().Length == 0)
        {
            var current = shown.FindIndex(_ => _.Branch is { } branch && IsCurrent(branch));
            if (current >= 0)
            {
                return current;
            }
        }

        return Selectable(0, 1);
    }

    /// <summary>
    /// The lines for <paramref name="listing"/>, those whose names match
    /// <paramref name="search"/>, under the heading of each group that has any. Null while the
    /// branches are still being found.
    /// </summary>
    internal static List<Row> RowsFor(GitBranches.Listing? listing, string search)
    {
        if (listing == null)
        {
            return [new("Finding branches…")];
        }

        if (listing.Error != null)
        {
            var reason = listing.Error.Split('\n', 2)[0];
            return [new($"Couldn't read the branches: {reason}")];
        }

        var rows = new List<Row>();
        List<GitBranches.Branch> defaultBranch = [];
        if (listing.Default != null)
        {
            defaultBranch.Add(listing.Default);
        }

        AddGroup(rows, "Default branch", defaultBranch, search);
        AddGroup(rows, "Recent branches", listing.Recent, search);
        AddGroup(rows, "Other branches", listing.Other, search);
        if (rows.Count > 0)
        {
            return rows;
        }

        if (search.Trim().Length == 0)
        {
            return [new("No branches")];
        }

        return [new("No branch matches. Enter makes a new one.")];
    }

    static void AddGroup(List<Row> rows, string heading, IReadOnlyList<GitBranches.Branch> branches, string search)
    {
        var matches = CommandPalette.Match(branches, _ => _.Name, search);
        if (matches.Count == 0)
        {
            return;
        }

        rows.Add(new(heading, Heading: true));
        rows.AddRange(matches.Select(_ => new Row(_.Name, _)));
    }

    const string stashedNote = "stashed changes";

    /// <summary>
    /// What is said after a branch's name: that it has changes stashed, waiting for it, and how
    /// long ago it was committed to.
    /// </summary>
    internal static string Note(GitBranches.Branch branch, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (branch.Stashed)
        {
            parts.Add(stashedNote);
        }

        if (branch.Committed is { } committed)
        {
            parts.Add(GitBranches.Ago(committed, now));
        }

        return string.Join(" · ", parts);
    }

    void DrawRow(object? sender, DrawItemEventArgs args)
    {
        if (args.Index < 0 ||
            args.Index >= shown.Count)
        {
            return;
        }

        var row = shown[args.Index];
        var bounds = Rectangle.Inflate(args.Bounds, -LogicalToDeviceUnits(6), 0);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (row.Branch is not { } branch)
        {
            // A heading or a message: never highlighted, as it can't be picked.
            using var background = new SolidBrush(list.BackColor);
            args.Graphics.FillRectangle(background, args.Bounds);
            var font = list.Font;
            if (row.Heading &&
                headingFont != null)
            {
                font = headingFont;
            }

            TextRenderer.DrawText(args.Graphics, row.Text, font, bounds, ExplorerStyle.SecondaryText, flags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            return;
        }

        args.DrawBackground();
        var selected = args.State.HasFlag(DrawItemState.Selected);
        var color = list.ForeColor;
        var dimmed = SystemColors.GrayText;
        if (selected)
        {
            color = ExplorerStyle.HighlightText;
            dimmed = ExplorerStyle.HighlightText;
        }

        // A column for the tick the branch checked out has, as a menu has for a ticked entry.
        var tick = LogicalToDeviceUnits(22);
        if (IsCurrent(branch))
        {
            var size = LogicalToDeviceUnits(12);
            var glyph = Icons.Bitmap(Icons.CheckMark, size, color);
            args.Graphics.DrawImage(glyph, bounds.Left, bounds.Top + (bounds.Height - size) / 2, size, size);
        }

        // The name comes first: the note has what room it leaves. Short of room, how long ago
        // goes before that changes are stashed, then the note is cut short, then left off.
        var space = bounds.Width - tick;
        var gap = LogicalToDeviceUnits(12);
        var room = space - Math.Min(Measure(branch.Name), space) - gap;
        var note = Note(branch, Time.GetUtcNow());
        if (branch.Stashed &&
            Measure(note) > room)
        {
            note = stashedNote;
        }

        var noteWidth = 0;
        if (note.Length > 0 &&
            room >= LogicalToDeviceUnits(24))
        {
            noteWidth = Math.Min(Measure(note), room);
            var noteBounds = Rectangle.FromLTRB(bounds.Right - noteWidth, bounds.Top, bounds.Right, bounds.Bottom);
            TextRenderer.DrawText(args.Graphics, note, list.Font, noteBounds, dimmed, flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            noteWidth += gap;
        }

        var name = Rectangle.FromLTRB(bounds.Left + tick, bounds.Top, bounds.Right - noteWidth, bounds.Bottom);
        TextRenderer.DrawText(args.Graphics, branch.Name, list.Font, name, color, flags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        int Measure(string text) =>
            TextRenderer.MeasureText(args.Graphics, text, list.Font, Size.Empty, flags).Width;
    }
}
