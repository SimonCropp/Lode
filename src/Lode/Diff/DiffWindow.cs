/// <summary>
/// The diff in a window of its own, as GitHub Desktop's History tab lays a commit out: along the
/// top, the commit's subject, who made it and its SHA, or that the changes are uncommitted; down
/// the left, the files changed, as the panes draw them; and the picked file's diff beside them.
/// One a window, shown again for whatever is asked for next.
/// </summary>
sealed class DiffWindow : Form
{
    // Pixels at 100% scale.
    const int listWidth = 300;

    Heading heading;
    PaneSplitContainer split = new()
    {
        Dock = DockStyle.Fill,
        Line = SplitterLine.Plain,
        FixedPanel = FixedPanel.Panel1
    };

    RowList files;
    RowTip fileTip;
    DiffView view;
    DiffRequest? request;
    IReadOnlyList<Change> changes = [];
    bool showingPicked;

    public DiffWindow(Settings settings)
    {
        Text = "Changes";
        StartPosition = FormStartPosition.Manual;
        BackColor = ExplorerStyle.Window;
        Size = new(LogicalToDeviceUnits(1150), LogicalToDeviceUnits(720));
        KeyPreview = true;
        view = new(settings)
        {
            Dock = DockStyle.Fill
        };
        files = new(DesktopStyle.RowHeight(Font))
        {
            MultiSelect = false,
            AccessibleName = "Files changed"
        };
        files.DrawRow += DrawFile;
        files.SelectedIndexChanged += (_, _) => ShowPicked();
        files.GotFocus += (_, _) => files.Invalidate();
        files.LostFocus += (_, _) => files.Invalidate();
        fileTip = new(files, _ => ChangeRows.TipAt(files, _, changes));
        heading = new(this);
        split.Panel1.Controls.Add(files);
        split.Panel2.Controls.Add(view);
        Controls.Add(split);
        Controls.Add(heading);
        split.SplitterDistance = LogicalToDeviceUnits(listWidth);
    }

    internal DiffView View => view;

    internal RowList Files => files;

    /// <summary>
    /// Shows what the pane asked for: its files down the left, the one asked for picked, its diff
    /// beside them.
    /// </summary>
    public void Show(DiffRequest asked)
    {
        if (asked.Source is not { } source)
        {
            return;
        }

        request = asked;
        changes = asked.Files;
        heading.Show(asked.Commit, source.Repo, changes.Count);
        Text = Title(asked.Commit, source.Repo);
        showingPicked = true;
        files.BeginUpdate();
        files.SelectedIndices.Clear();
        files.SetRows(changes.Count);
        var index = -1;
        for (var at = 0; at < changes.Count; at++)
        {
            if (changes[at].Path == source.File.Path)
            {
                index = at;
                break;
            }
        }

        if (index >= 0)
        {
            files.SelectedIndices.Add(index);
            files.FocusedItem = files.Items[index];
            files.EnsureVisible(index);
        }

        files.EndUpdate();
        files.Invalidate();
        showingPicked = false;
        view.Show(source);
    }

    static string Title(GitCommit? commit, string repo)
    {
        var name = Path.GetFileName(repo.TrimEnd('\\'));
        if (commit == null)
        {
            return $"Uncommitted changes – {name}";
        }

        return $"{commit.Subject} – {name}";
    }

    // A file picked down the left: its diff, of the same change as the one asked for.
    void ShowPicked()
    {
        if (showingPicked ||
            request?.Source is not { } asked ||
            files.SelectedIndices.Count != 1)
        {
            return;
        }

        var index = files.SelectedIndices[0];
        if (index >= changes.Count)
        {
            return;
        }

        view.Show(asked with
        {
            File = changes[index]
        });
    }

    /// <summary>
    /// Reads the diff again, for an uncommitted change of <paramref name="repo"/> read again.
    /// </summary>
    public void ChangesRead(string repo)
    {
        if (view.Source is { IsUncommitted: true } source &&
            Locations.Same(source.Repo, repo))
        {
            view.Reload();
        }
    }

    void DrawFile(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= changes.Count)
        {
            return;
        }

        var (background, text, secondary, onBlue) = files.Colours(index);
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        RowList.DrawRowLine(graphics, bounds);
        var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
        var room = Rectangle.FromLTRB(bounds.Left + inset, bounds.Top, bounds.Right, bounds.Bottom);
        ChangeRows.Draw(graphics, this, files.Font, room, changes[index], text, secondary, onBlue);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fileTip.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Along the top: the commit's subject, semibold, and under it who made it and its SHA; or
    /// that the changes are uncommitted, and in which repository. How many files changed beneath.
    /// </summary>
    sealed class Heading : Control
    {
        Font title;
        GitCommit? commit;
        string repo = "";
        int count;

        public Heading(Control owner)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Dock = DockStyle.Top;
            title = DesktopStyle.Semibold(owner.Font, owner.Font.SizeInPoints * 7 / 6);
            Height = title.Height + owner.Font.Height + owner.LogicalToDeviceUnits(24);
            BackColor = DesktopStyle.AltBackground;
        }

        public void Show(GitCommit? shown, string shownRepo, int files)
        {
            commit = shown;
            repo = shownRepo;
            count = files;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var graphics = args.Graphics;
            graphics.Clear(DesktopStyle.AltBackground);
            using (var pen = new Pen(DesktopStyle.Border))
            {
                graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
            var top = LogicalToDeviceUnits(10);
            var name = Path.GetFileName(repo.TrimEnd('\\'));
            var subject = "Uncommitted changes";
            var details = name;
            if (commit != null)
            {
                subject = commit.Subject;
                details = $"{commit.Author} · {commit.ShortSha} · {name}";
            }

            details = $"{details} · {GitState.CountOf(count, "changed file")}";
            TextRenderer.DrawText(graphics, subject, title, new Rectangle(inset, top, Width - inset * 2, title.Height), DesktopStyle.Text, flags);
            TextRenderer.DrawText(graphics, details, Font, new Rectangle(inset, top + title.Height + LogicalToDeviceUnits(2), Width - inset * 2, Font.Height), DesktopStyle.SecondaryText, flags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                title.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
