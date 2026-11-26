/// <summary>
/// Picks a branch for Merge, Squash and merge, Rebase or Compare to branch, as GitHub Desktop's
/// dialogs for them do: a box to filter the branches by, the branches but the one checked out,
/// and under them what it would do with the branch picked, read as it is picked, the button
/// enabled only where that can go ahead. For a rebase that rewrites commits origin has, whether
/// to force push afterwards.
/// </summary>
class BranchActionForm : Form
{
    FrameTextBox filter;
    ListBox list;
    RichTextBox preview;
    CheckBox forcePush;
    Button accept;
    List<GitBranches.Branch> branches;
    List<GitBranches.Branch> shown = [];
    Func<string, Cancel, GitMerging.Preview> previewOf;
    int previewVersion;

    // Whether force push is asked: its box is hidden once the dialog closes, so its own
    // Visible says nothing then.
    bool forceOffered;
    CancellationTokenSource? previewCancel;

    /// <summary>
    /// The name of the branch picked, origin/feature for one only on origin.
    /// </summary>
    public string? Branch { get; private set; }

    /// <summary>
    /// Whether to force push once rebased, asked only where origin has commits it rewrites.
    /// </summary>
    public bool ForcePush => forceOffered && forcePush.Checked;

    /// <summary>
    /// Whether force push is asked, for the branch picked. For tests.
    /// </summary>
    internal bool OffersForcePush => forceOffered;

    /// <summary>
    /// The reading of what it would do with the branch picked, for tests to wait on.
    /// </summary>
    internal Task Previewing { get; private set; } = Task.CompletedTask;

    internal string PreviewText => preview.Text;

    internal bool CanAccept => accept.Enabled;

    internal IReadOnlyList<string> Listed => shown.Select(_ => _.Name).ToList();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Filter
    {
        get => filter.Text;
        set => filter.Text = value;
    }

    /// <param name="previewOf">What it would do with a branch, by its name: read off the UI
    /// thread.</param>
    /// <param name="picked">The branch to pick first, one right-clicked say.</param>
    public BranchActionForm(string title, string acceptText, GitBranches.Listing listing, Func<string, Cancel, GitMerging.Preview> previewOf, string? picked = null)
    {
        this.previewOf = previewOf;
        branches = BranchesOf(listing);

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

        // Drawn as the status bar's list of branches draws its filter, and the Clone dialog's.
        var filterFrame = new FramedTextBox
        {
            Outlined = true,
            Width = 420,
            Margin = new(3, 3, 3, 6)
        };
        filter = filterFrame.Box;
        filter.Name = "filter";
        filter.PlaceholderText = "Filter";
        filter.AccessibleName = "Filter branches";
        list = new()
        {
            Name = "branches",
            Width = 420,
            Height = 220,
            IntegralHeight = false,
            AccessibleName = "Branches"
        };

        // Selectable, for a file in conflict to be copied, and as tall as seven lines, scrolling
        // past them, a scrollbar only then, so the dialog stays the size it opened.
        preview = new()
        {
            Name = "preview",
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            TabStop = false,
            DetectUrls = false,
            Width = 420,
            Margin = new(3, 9, 3, 3)
        };
        preview.Height = preview.Font.Height * 7 + 4;
        forcePush = new()
        {
            Name = "forcePush",
            Text = "Force push to origin afterwards",
            AutoSize = true,
            Visible = false
        };
        accept = new()
        {
            Name = "accept",
            Text = acceptText,
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Enabled = false
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };
        AcceptButton = accept;
        CancelButton = cancel;

        filter.TextChanged += (_, _) => Refill(Branch);
        filter.KeyDown += (_, args) => MoveWithKeys(args);
        list.SelectedIndexChanged += (_, _) => Picked();
        list.DoubleClick += (_, _) =>
        {
            if (accept.Enabled)
            {
                DialogResult = DialogResult.OK;
            }
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            Margin = new(0, 9, 0, 0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(accept);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Location = new(Padding.Left, Padding.Top)
        };
        layout.Controls.Add(filterFrame);
        layout.Controls.Add(list);
        layout.Controls.Add(preview);
        layout.Controls.Add(forcePush);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        Refill(picked);
        if (shown.Count == 0)
        {
            preview.Text = "There is no other branch.";
        }
    }

    /// <summary>
    /// The branches to pick from, as the status bar lists them, the default branch first, then
    /// those switched to recently, then the rest, less the one checked out.
    /// </summary>
    internal static List<GitBranches.Branch> BranchesOf(GitBranches.Listing listing)
    {
        var all = new List<GitBranches.Branch>();
        if (listing.Default != null)
        {
            all.Add(listing.Default);
        }

        all.AddRange(listing.Recent);
        all.AddRange(listing.Other);
        return all
            .Where(_ => _.Remote || _.Name != listing.Current)
            .ToList();
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        filter.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs args)
    {
        previewCancel?.Cancel();
        base.OnFormClosed(args);
    }

    // Up and Down in the filter move through the branches, as the status bar's list has them.
    void MoveWithKeys(KeyEventArgs args)
    {
        if (shown.Count == 0)
        {
            return;
        }

        var step = args.KeyCode switch
        {
            Keys.Down => 1,
            Keys.Up => -1,
            _ => 0
        };
        if (step == 0)
        {
            return;
        }

        args.Handled = true;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + step, 0, shown.Count - 1);
    }

    // The branches matching what is typed, best first, the one picked before still picked where
    // it still shows, otherwise the first.
    void Refill(string? keep)
    {
        shown = CommandPalette.Match(branches, _ => _.Name, filter.Text).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var branch in shown)
        {
            list.Items.Add(branch.Name);
        }

        list.EndUpdate();
        var index = shown.FindIndex(_ => _.Name == keep);
        if (index < 0 &&
            shown.Count > 0)
        {
            index = 0;
        }

        list.SelectedIndex = index;
        if (index < 0)
        {
            Picked();
        }
    }

    void Picked()
    {
        var index = list.SelectedIndex;
        if (index < 0 ||
            index >= shown.Count)
        {
            Branch = null;
            previewCancel?.Cancel();
            previewVersion++;
            accept.Enabled = false;
            forceOffered = false;
            forcePush.Visible = false;
            preview.Text = "";
            return;
        }

        var name = shown[index].Name;
        if (name == Branch &&
            Previewing.IsCompleted &&
            preview.Text.Length > 0)
        {
            return;
        }

        Branch = name;
        Previewing = ShowPreview(name);
    }

    async Task ShowPreview(string name)
    {
        previewCancel?.Cancel();
        var source = new CancellationTokenSource();
        previewCancel = source;
        var asked = ++previewVersion;
        accept.Enabled = false;
        preview.Text = "Checking…";
        GitMerging.Preview read;
        try
        {
            read = await Task.Run(() => previewOf(name, source.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (asked != previewVersion ||
            IsDisposed)
        {
            return;
        }

        preview.Text = read.Text;
        accept.Enabled = read.CanGo;
        forceOffered = read.OnOrigin > 0;
        forcePush.Visible = forceOffered;
    }
}
