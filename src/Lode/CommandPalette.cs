/// <summary>
/// Ctrl+Shift+P: a box to type part of a command's name into, over a list of the commands that
/// match. Enter runs the highlighted one; Escape, or clicking elsewhere, closes it.
/// </summary>
sealed class CommandPalette : Form
{
    public record Command(string Name, Action Action, string Shortcut = "");

    readonly IReadOnlyList<Command> commands;
    readonly TextBox searchBox = new();
    readonly ListBox list = new();
    List<Command> shown = [];
    bool closing;

    /// <summary>
    /// The command picked, once the palette has closed. Null when it was dismissed.
    /// </summary>
    public Command? Chosen { get; private set; }

    public CommandPalette(IReadOnlyList<Command> commands)
    {
        this.commands = commands;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false;
        Text = "";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(560, 360);
        Padding = new(6);

        searchBox.Dock = DockStyle.Top;
        searchBox.AccessibleName = "Command";
        searchBox.PlaceholderText = "Type a command";
        searchBox.TextChanged += (_, _) => Refill();

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.IntegralHeight = false;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.DrawItem += DrawCommand;
        list.MouseClick += (_, eventArgs) =>
        {
            var index = list.IndexFromPoint(eventArgs.Location);
            if (index >= 0)
            {
                Choose(shown[index]);
            }
        };

        var gap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 6
        };
        Controls.Add(list);
        Controls.Add(gap);
        Controls.Add(searchBox);
        Refill();
    }

    /// <summary>
    /// Shows the palette near the top of <paramref name="owner"/>, centred, and runs the chosen
    /// command once it has closed.
    /// </summary>
    public static void Show(Form owner, IReadOnlyList<Command> commands)
    {
        var palette = new CommandPalette(commands);
        var area = owner.RectangleToScreen(owner.ClientRectangle);
        palette.Location = new(
            area.Left + (area.Width - palette.Width) / 2,
            area.Top + owner.LogicalToDeviceUnits(40));
        palette.FormClosed += (_, _) =>
        {
            var chosen = palette.Chosen;
            owner.BeginInvoke(
                () =>
                {
                    palette.Dispose();
                    chosen?.Action();
                });
        };
        palette.Show(owner);
    }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        list.ItemHeight = Math.Max(list.Font.Height + LogicalToDeviceUnits(8), list.ItemHeight);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        searchBox.Focus();
    }

    protected override void OnDeactivate(EventArgs eventArgs)
    {
        base.OnDeactivate(eventArgs);
        CloseOnce();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                CloseOnce();
                return true;
            case Keys.Enter:
                if (list.SelectedIndex >= 0)
                {
                    Choose(shown[list.SelectedIndex]);
                }

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
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    int VisibleRows => Math.Max(1, list.ClientSize.Height / list.ItemHeight);

    void MoveSelection(int offset)
    {
        if (shown.Count > 0)
        {
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + offset, 0, shown.Count - 1);
        }
    }

    void Choose(Command command)
    {
        Chosen = command;
        CloseOnce();
    }

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
        shown = Match(commands, searchBox.Text);
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var command in shown)
        {
            list.Items.Add(command.Name);
        }

        if (shown.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        list.EndUpdate();
    }

    void DrawCommand(object? sender, DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0 ||
            eventArgs.Index >= shown.Count)
        {
            return;
        }

        eventArgs.DrawBackground();
        var command = shown[eventArgs.Index];
        var selected = eventArgs.State.HasFlag(DrawItemState.Selected);
        var color = selected ? SystemColors.HighlightText : list.ForeColor;
        var dimmed = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        var bounds = Rectangle.Inflate(eventArgs.Bounds, -LogicalToDeviceUnits(6), 0);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(eventArgs.Graphics, command.Name, list.Font, bounds, color, flags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (command.Shortcut.Length > 0)
        {
            TextRenderer.DrawText(eventArgs.Graphics, command.Shortcut, list.Font, bounds, dimmed, flags | TextFormatFlags.Right);
        }
    }

    /// <summary>
    /// The commands whose names hold every word of <paramref name="search"/>, ignoring case.
    /// Those with a word starting with the first search word come first; otherwise the order is kept.
    /// </summary>
    internal static List<Command> Match(IReadOnlyList<Command> commands, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return commands.ToList();
        }

        return commands
            .Where(command => words.All(word => command.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(command => !StartsAWord(command.Name, words[0]))
            .ToList();
    }

    // "tab" starts a word of "Close tab" and "New tab", but not of "Stable".
    static bool StartsAWord(string name, string word)
    {
        foreach (var part in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(word, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
