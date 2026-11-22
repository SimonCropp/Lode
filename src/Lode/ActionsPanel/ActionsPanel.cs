/// <summary>
/// Under the folder tree: the commands running or finished (git, NuGet updates), one line each,
/// from <see cref="ActionLog"/>. Hidden while there are none. Nothing pops up when a command
/// finishes: click its line for everything it reported; right-click to remove lines.
/// </summary>
sealed class ActionsPanel : Panel
{
    const int maximumVisibleRows = 5;

    Label header = new()
    {
        Text = "Actions",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new(4, 0, 0, 0)
    };

    LinkLabel clear = new()
    {
        Name = "clearActions",
        Text = "Clear",
        // Sized in Fit: an auto-sized label sits at the top of the row rather than its middle.
        AutoSize = false,
        Dock = DockStyle.Right,
        TextAlign = ContentAlignment.MiddleRight,
        Padding = new(0, 0, 4, 0)
    };

    ListBox list = new()
    {
        Name = "actions",
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawFixed,
        IntegralHeight = false
    };

    Panel headerRow = new()
    {
        Dock = DockStyle.Top
    };

    // Turns the rings beside running actions, only while there are any and the panel shows.
    Timer spinner = new()
    {
        Interval = 30
    };

    /// <summary>
    /// Milliseconds from some fixed point, by which the ring beside a running action turns.
    /// Tests hold it still.
    /// </summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    public ActionsPanel()
    {
        Dock = DockStyle.Bottom;
        Visible = false;
        BackColor = ExplorerStyle.Window;
        // The top row of pixels is the line between the tree and the panel, which the header
        // would otherwise cover.
        Padding = new(0, 1, 0, 0);
        spinner.Tick += (_, _) => Spin();

        // Its scrollbar drawn as the folder's and tree's are: a thin thumb, which widens and
        // shows its arrows under the mouse.
        list.HandleCreated += (_, _) => SetWindowTheme(list.Handle, ExplorerStyle.ControlTheme, null);

        // Clear ends where the rows below it do, which is short of the panel's edge once the
        // list has more entries than fit and shows its scrollbar. Docked to the edge regardless,
        // it sat over the scrollbar.
        list.ClientSizeChanged += (_, _) =>
            headerRow.Padding = new(0, 0, list.Width - list.ClientSize.Width, 0);

        headerRow.Controls.Add(header);
        headerRow.Controls.Add(clear);
        Controls.Add(list);
        Controls.Add(headerRow);

        clear.LinkClicked += (_, _) => ActionLog.ClearFinished();
        list.DrawItem += DrawEntry;
        list.MouseClick += (_, args) =>
        {
            if (args.Button != MouseButtons.Left)
            {
                return;
            }

            var index = list.IndexFromPoint(args.Location);
            if (index >= 0 &&
                list.Items[index] is ActionLog.Entry { State: not ActionLog.State.Running } entry)
            {
                ShowDetails(entry);
            }
        };
        list.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                ShowDetails(list.SelectedItem as ActionLog.Entry);
            }
            else if (args.KeyCode == Keys.Delete &&
                     list.SelectedItem is ActionLog.Entry entry)
            {
                ActionLog.Remove(entry);
            }
        };
        list.MouseUp += OnListMouseUp;

        ActionLog.Changed += Refill;
        ActionLog.Progressed += Repaint;
        Refill();
    }

    // Progress changes only an entry's second line, so it is drawn again rather than refilled.
    void Repaint(ActionLog.Entry entry)
    {
        var index = list.Items.IndexOf(entry);
        if (index >= 0)
        {
            list.Invalidate(list.GetItemRectangle(index));
        }
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        Fit();
    }

    void Refill()
    {
        var selected = list.SelectedItem;
        using (list.SuspendPainting())
        {
            list.Items.Clear();

            // Newest first, so what was just started is at the top.
            foreach (var entry in ActionLog.Entries.Reverse())
            {
                list.Items.Add(entry);
            }

            if (selected != null &&
                list.Items.Contains(selected))
            {
                list.SelectedItem = selected;
            }
        }

        Visible = list.Items.Count > 0;

        // Clear removes finished lines only, so it has nothing to do while everything runs.
        clear.Enabled = ActionLog.Entries.Any(_ => _.State != ActionLog.State.Running);
        Fit();
        list.Invalidate();
        UpdateSpinner();
    }

    // Also when the window goes to the tray, or comes back.
    protected override void OnVisibleChanged(EventArgs args)
    {
        base.OnVisibleChanged(args);
        UpdateSpinner();
    }

    void UpdateSpinner() =>
        spinner.Enabled = Visible &&
                          ActionLog.Entries.Any(_ => _.State == ActionLog.State.Running);

    internal bool Spinning => spinner.Enabled;

    // Each running action's ring drawn at its next turn, straight onto the list. Invalidated, the
    // list would wipe the ring to the background before drawing it again, a flicker thirty times
    // a second.
    void Spin()
    {
        using var graphics = list.CreateGraphics();
        for (var index = list.TopIndex; index < list.Items.Count; index++)
        {
            var bounds = list.GetItemRectangle(index);
            if (bounds.Top >= list.ClientSize.Height)
            {
                break;
            }

            if (list.Items[index] is ActionLog.Entry { State: ActionLog.State.Running })
            {
                DrawRing(graphics, MarkBounds(bounds, list.Font), index == list.SelectedIndex);
            }
        }
    }

    // Where an entry's tick, cross or ring goes: beside its first line.
    Rectangle MarkBounds(Rectangle entry, Font font) =>
        new(entry.Left, entry.Top + LogicalToDeviceUnits(4), LogicalToDeviceUnits(20), font.Height);

    /// <summary>
    /// Windows' progress ring: an arc in the accent colour that turns, growing and shrinking as
    /// it goes. Drawn through a bitmap with the entry's background, so it never shows half drawn.
    /// </summary>
    void DrawRing(Graphics graphics, Rectangle mark, bool selected)
    {
        var background = list.BackColor;
        var colour = SystemColors.Highlight;
        if (selected)
        {
            background = SystemColors.Highlight;
            colour = ExplorerStyle.HighlightText;
        }

        using var bitmap = new Bitmap(mark.Width, mark.Height);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.Clear(background);
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            var thickness = LogicalToDeviceUnits(2);
            var diameter = Math.Min(mark.Width, mark.Height) * 0.75f - thickness;
            var circle = new RectangleF((mark.Width - diameter) / 2, (mark.Height - diameter) / 2, diameter, diameter);

            // A turn a second, the arc growing and shrinking over two.
            var milliseconds = Clock();
            var turn = milliseconds % 1000 / 1000f * 360;
            var sweep = 140 + 100 * MathF.Sin(milliseconds % 2000 / 2000f * 2 * MathF.PI);
            using var pen = new Pen(colour, thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            canvas.DrawArc(pen, circle, turn - 90, sweep);
        }

        graphics.DrawImageUnscaled(bitmap, mark.Location);
    }

    // Tall enough for every entry, up to a few; the list scrolls beyond that. Each entry is two
    // lines, so the status isn't the part cut off in a narrow tree.
    void Fit()
    {
        var padding = LogicalToDeviceUnits(4);
        list.ItemHeight = Font.Height * 2 + padding * 2;
        headerRow.Height = Font.Height + padding * 2;
        clear.Width = TextRenderer.MeasureText(clear.Text, Font).Width + padding * 2;
        var rows = Math.Clamp(list.Items.Count, 1, maximumVisibleRows);
        Height = headerRow.Height + rows * list.ItemHeight + 2;
    }

    // A line between the tree above and the panel.
    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        using var pen = new Pen(SystemColors.ControlLight);
        args.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }

    void DrawEntry(object? sender, DrawItemEventArgs args)
    {
        args.DrawBackground();
        if (args.Index < 0 || list.Items[args.Index] is not ActionLog.Entry entry)
        {
            return;
        }

        var selected = (args.State & DrawItemState.Selected) != 0;
        var bounds = args.Bounds;
        var font = args.Font ?? Font;

        // Green tick, red cross, or the progress ring while it runs.
        var mark = MarkBounds(bounds, font);
        if (entry.State == ActionLog.State.Running)
        {
            DrawRing(args.Graphics, mark, selected);
        }
        else
        {
            var glyph = "✓";
            var colour = Color.ForestGreen;
            if (entry.State == ActionLog.State.Failed)
            {
                glyph = "✗";
                colour = Color.Firebrick;
            }

            if (selected)
            {
                colour = ExplorerStyle.HighlightText;
            }

            TextRenderer.DrawText(
                args.Graphics,
                glyph,
                font,
                mark,
                colour,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // What and where on the first line, how it went on the second.
        var padding = LogicalToDeviceUnits(4);
        var lineHeight = font.Height;
        var left = mark.Right;
        var right = bounds.Right - 2;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        var titleColour = selected ? ExplorerStyle.HighlightText : SystemColors.ControlText;
        TextRenderer.DrawText(
            args.Graphics,
            $"{entry.Title} – {entry.FolderName}",
            font,
            Rectangle.FromLTRB(left, bounds.Top + padding, right, bounds.Top + padding + lineHeight),
            titleColour,
            flags);

        var statusColour = SystemColors.GrayText;
        if (selected)
        {
            statusColour = ExplorerStyle.HighlightText;
        }
        else if (entry.State == ActionLog.State.Failed)
        {
            statusColour = Color.Firebrick;
        }

        TextRenderer.DrawText(
            args.Graphics,
            entry.Summary,
            font,
            Rectangle.FromLTRB(left, bounds.Top + padding + lineHeight, right, bounds.Bottom - padding),
            statusColour,
            flags);
    }

    void OnListMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right)
        {
            return;
        }

        var index = list.IndexFromPoint(args.Location);
        if (index < 0 || list.Items[index] is not ActionLog.Entry entry)
        {
            return;
        }

        list.SelectedIndex = index;
        var menu = new ExplorerMenu();
        menu.Items.Add("Show details", null, (_, _) => ShowDetails(entry));
        if (entry.Cancellation != null)
        {
            menu.Items.Add("Cancel", null, (_, _) => entry.Cancel()).Enabled = entry.CanCancel;
        }

        menu.Items.Add("Remove", null, (_, _) => ActionLog.Remove(entry)).Enabled = entry.State != ActionLog.State.Running;
        menu.Items.Add("Clear finished", null, (_, _) => ActionLog.ClearFinished());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(list, args.Location);
    }

    // Modeless, so the rest of Lode stays usable while it is open. One window per entry:
    // clicking the line again brings it forward.
    void ShowDetails(ActionLog.Entry? entry)
    {
        if (entry == null)
        {
            return;
        }

        var open = Application.OpenForms
            .OfType<ActionDetailsForm>()
            .FirstOrDefault(_ => _.Entry == entry);
        if (open != null)
        {
            open.Activate();
            return;
        }

        new ActionDetailsForm(entry).Show(FindForm());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ActionLog.Changed -= Refill;
            ActionLog.Progressed -= Repaint;
            spinner.Dispose();
        }

        base.Dispose(disposing);
    }

    internal sealed class ActionDetailsForm : Form
    {
        public ActionLog.Entry Entry { get; }

        public ActionDetailsForm(ActionLog.Entry entry)
        {
            Entry = entry;
            AutoScaleDimensions = new(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = $"{entry.Title} – {entry.Where}";
            ShowInTaskbar = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new(720, 420);

            var details = entry.Details;
            if (details.Length == 0)
            {
                details = entry.Summary;
            }
            else
            {
                details = $"{entry.Summary}{Environment.NewLine}{Environment.NewLine}{details}";
            }

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new("Consolas", Font.SizeInPoints),
                BackColor = ExplorerStyle.Window,
                Text = details.ReplaceLineEndings(Environment.NewLine)
            };
            Controls.Add(text);
            KeyPreview = true;
            KeyDown += (_, args) =>
            {
                if (args.KeyCode == Keys.Escape)
                {
                    Close();
                }
            };
        }
    }
}
