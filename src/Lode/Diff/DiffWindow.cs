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
    sealed class Heading : Panel
    {
        Font title;

        Label subject = new()
        {
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            ForeColor = DesktopStyle.Text
        };

        Label details = new()
        {
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            ForeColor = DesktopStyle.SecondaryText
        };

        public Heading(Control owner)
        {
            Dock = DockStyle.Top;
            title = DesktopStyle.Semibold(owner.Font, owner.Font.SizeInPoints * 7 / 6);
            subject.Font = title;
            Height = title.Height + owner.Font.Height + owner.LogicalToDeviceUnits(24);
            BackColor = DesktopStyle.AltBackground;
            Controls.Add(subject);
            Controls.Add(details);
        }

        public void Show(GitCommit? shown, string shownRepo, int files)
        {
            var name = Path.GetFileName(shownRepo.TrimEnd('\\'));
            subject.Text = "Uncommitted changes";
            var said = name;
            if (shown != null)
            {
                subject.Text = shown.Subject;
                said = $"{shown.Author} · {shown.ShortSha} · {name}";
            }

            details.Text = $"{said} · {GitState.CountOf(files, "changed file")}";
        }

        // The subject, then the details under it, across all but the inset either side, each
        // text starting at the inset.
        protected override void OnLayout(LayoutEventArgs args)
        {
            base.OnLayout(args);
            var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
            var top = LogicalToDeviceUnits(10);
            Place(subject, inset, top, title.Height);
            Place(details, inset, subject.Bottom + LogicalToDeviceUnits(2), Font.Height);
        }

        void Place(Label label, int inset, int top, int height)
        {
            var padding = DesktopStyle.LabelPadding(label.Font);
            label.SetBounds(inset - padding, top, Math.Max(0, Width - inset * 2 + padding * 2), height);
        }

        // The line along the foot.
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using var pen = new Pen(DesktopStyle.Border);
            args.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnResize(EventArgs args)
        {
            base.OnResize(args);
            Invalidate();
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
