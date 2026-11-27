/// <summary>
/// Picks the branch Switch branch switches to: a button for each, the one with the most recent
/// commit first, pressing one picking it. Each says, as its tooltip, what switching to it would
/// do. Past as many as fit, the buttons scroll, the dialog staying the size it opened.
/// </summary>
class SwitchBranchForm : Form
{
    const int width = 420;
    const int maxShown = 12;

    /// <summary>
    /// The name of the branch picked, as each repository checks it out.
    /// </summary>
    public string? Branch { get; private set; }

    internal IReadOnlyList<Button> Buttons { get; }

    /// <param name="describe">What switching to a branch would do, by its name.</param>
    public SwitchBranchForm(string title, string heading, IReadOnlyList<GitBranches.Branch> branches, Func<string, string> describe)
    {
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new(9);

        var label = new Label
        {
            AutoSize = true,
            MaximumSize = new(width, 0),
            Margin = new(3, 3, 3, 6),
            Text = heading
        };

        var tips = new ToolTip();
        Disposed += (_, _) => tips.Dispose();
        var buttonHeight = Font.Height + 14;
        var panel = new FlowLayoutPanel
        {
            Name = "branches",
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Width = width + SystemInformation.VerticalScrollBarWidth,
            Height = (buttonHeight + 4) * Math.Min(branches.Count, maxShown),
            Margin = new(0),
            AccessibleName = "Branches"
        };

        var buttons = new List<Button>();
        var buttonWidth = width - 6;
        if (branches.Count <= maxShown)
        {
            panel.Width = width;
        }

        foreach (var branch in branches)
        {
            var button = new Button
            {
                Text = branch.Name,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Width = buttonWidth,
                Height = buttonHeight,
                Margin = new(3, 2, 3, 2),
                UseMnemonic = false
            };
            var description = describe(branch.Name);
            tips.SetToolTip(button, description);
            button.AccessibleDescription = description;
            button.Click += (_, _) =>
            {
                Branch = branch.Name;
                DialogResult = DialogResult.OK;
            };
            panel.Controls.Add(button);
            buttons.Add(button);
        }

        Buttons = buttons;

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new(3, 9, 3, 3)
        };
        CancelButton = cancel;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Location = new(Padding.Left, Padding.Top)
        };
        layout.Controls.Add(label);
        layout.Controls.Add(panel);
        layout.Controls.Add(cancel);
        Controls.Add(layout);
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        if (Buttons.Count > 0)
        {
            Buttons[0].Focus();
        }
    }
}
