/// <summary>
/// What Changes shows in place of its files when nothing is uncommitted, as GitHub Desktop's No
/// local changes does: saying so, and, where there is something to do next, suggesting it, the
/// commits to pull, those not pushed, or, in a fork, those to sync, with a button that asks about
/// them as the status bar's part does.
/// </summary>
sealed class NoChanges : Panel
{
    // Pixels at 100% scale.
    const int top = 28;
    const int cardInset = 12;
    const int buttonHeight = 28;

    Label heading = new()
    {
        AutoSize = true,
        UseMnemonic = false,
        Text = "No local changes"
    };

    Label text = new()
    {
        AutoSize = true,
        UseMnemonic = false,
        Text = "Nothing in the repository is uncommitted."
    };

    // The suggestion, in a box with rounded corners, the button under what it says.
    Panel card = new()
    {
        Visible = false
    };

    Label suggestionText = new()
    {
        AutoSize = true,
        UseMnemonic = false
    };

    AccentButton button = new();
    Font headingFont;
    Suggestion? suggestion;
    bool laying;

    /// <summary>
    /// The suggestion's button was clicked, for the part of the repository's state it is about to
    /// be asked about.
    /// </summary>
    public event Action<GitState.PartKind>? ActRequested;

    /// <summary>
    /// What to do next, and the part of the state it is about.
    /// </summary>
    internal sealed record Suggestion(string Text, string Button, GitState.PartKind Part);

    public NoChanges()
    {
        BackColor = ExplorerStyle.Window;
        headingFont = HeadingFont(Font);
        heading.Font = headingFont;
        heading.ForeColor = DesktopStyle.Text;
        text.ForeColor = DesktopStyle.SecondaryText;
        suggestionText.ForeColor = DesktopStyle.Text;
        card.BackColor = ExplorerStyle.Window;
        card.Paint += PaintCard;
        card.Resize += (_, _) => card.Invalidate();
        card.Controls.Add(suggestionText);
        card.Controls.Add(button);
        button.Click += (_, _) =>
        {
            if (suggestion is { } asked)
            {
                ActRequested?.Invoke(asked.Part);
            }
        };
        Controls.Add(heading);
        Controls.Add(text);
        Controls.Add(card);
    }

    // Desktop's heading, larger and semibold.
    static Font HeadingFont(Font font) =>
        DesktopStyle.Semibold(font, font.SizeInPoints * 1.4f);

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        headingFont.Dispose();
        headingFont = HeadingFont(Font);
        heading.Font = headingFont;
    }

    /// <summary>
    /// The suggestion showing, null for none. For tests.
    /// </summary>
    internal Suggestion? Suggested => suggestion;

    internal Button SuggestionButton => button;

    /// <summary>
    /// Suggests what to do next for the repository's state as last read.
    /// </summary>
    public void ShowState(GitState? state)
    {
        var next = SuggestionFor(state);
        if (next == suggestion)
        {
            return;
        }

        suggestion = next;
        card.Visible = next != null;
        if (next != null)
        {
            suggestionText.Text = next.Text;
            button.Text = next.Button;
            button.AccessibleDescription = next.Text;
        }

        PerformLayout();
    }

    /// <summary>
    /// What to do next, with nothing uncommitted: bring in the commits to pull, which, where
    /// there are commits to push too, Pull then push sends after them; send those not pushed; or,
    /// in a fork, bring in those of the repository it was made from. None where there is nothing
    /// to do.
    /// </summary>
    internal static Suggestion? SuggestionFor(GitState? state)
    {
        if (state == null)
        {
            return null;
        }

        if (state is
            {
                Behind: > 0,
                Ahead: > 0
            })
        {
            return new(
                $"{GitState.CountOf(state.Behind, "commit")} to pull, and {GitState.CountOf(state.Ahead, "commit")} here to push after them, as of the last fetch.",
                "Pull…",
                GitState.PartKind.Behind);
        }

        if (state.Behind > 0)
        {
            return new($"{GitState.CountOf(state.Behind, "commit")} to pull, as of the last fetch.", "Pull…", GitState.PartKind.Behind);
        }

        if (state.Ahead > 0)
        {
            return new($"{GitState.CountOf(state.Ahead, "commit")} not pushed yet.", "Push…", GitState.PartKind.Ahead);
        }

        if (state.ForkBehind > 0)
        {
            return new(
                $"{GitState.CountOf(state.ForkBehind, "commit")} on the repository this was forked from to bring in, as of the last fetch.",
                "Sync fork…",
                GitState.PartKind.ForkBehind);
        }

        return null;
    }

    // Down from the top, each as wide as the pane, the text wrapping to fit.
    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        if (laying)
        {
            return;
        }

        laying = true;
        try
        {
            var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
            var width = Math.Max(1, ClientSize.Width - inset * 2);
            heading.MaximumSize = new(width, 0);
            heading.Location = new(inset, LogicalToDeviceUnits(top));
            text.MaximumSize = new(width, 0);
            text.Location = new(inset, heading.Bottom + LogicalToDeviceUnits(6));
            if (suggestion == null)
            {
                return;
            }

            var padding = LogicalToDeviceUnits(cardInset);
            suggestionText.MaximumSize = new(Math.Max(1, width - padding * 2), 0);
            suggestionText.Location = new(padding, padding);
            var textWidth = TextRenderer.MeasureText(button.Text, button.Font).Width;
            button.SetBounds(padding, suggestionText.Bottom + LogicalToDeviceUnits(10), textWidth + LogicalToDeviceUnits(32), LogicalToDeviceUnits(buttonHeight));
            card.SetBounds(inset, text.Bottom + LogicalToDeviceUnits(18), width, button.Bottom + padding);
        }
        finally
        {
            laying = false;
        }
    }

    // A rounded border, as Desktop draws round its suggestions.
    void PaintCard(object? sender, PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = ExplorerStyle.RoundedRectangle(new(0, 0, card.Width - 1, card.Height - 1), LogicalToDeviceUnits(DesktopStyle.Radius));
        using var pen = new Pen(DesktopStyle.Border);
        graphics.DrawPath(pen, shape);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headingFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
