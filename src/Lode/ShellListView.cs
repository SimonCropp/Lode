/// <summary>
/// A ListView with the Explorer look, shell icons with Explorer's row spacing, and a
/// context-menu event that distinguishes mouse from keyboard.
/// </summary>
sealed class ShellListView : ListView
{
    // Explorer's spacing, in pixels at 100% scale.
    const int rowHeight = 30;
    const int paddingBeforeIcon = 10;
    const int paddingAfterIcon = 4;
    const int unscaledIconSize = 16;

    readonly ImageList icons = new() { ColorDepth = ColorDepth.Depth32Bit };
    readonly Dictionary<int, int> paddedIcons = [];
    readonly Point iconOffset;

    int sortArrowColumn = -1;
    bool sortArrowAscending;

    public event Action<Point, bool>? ContextMenuRequested;

    /// <summary>
    /// The column header was right-clicked, at this point on the screen, or the context menu key
    /// pressed while it had the focus, in which case the point is the header's top left.
    /// </summary>
    public event Action<Point>? HeaderContextMenuRequested;

    public ShellListView()
    {
        DoubleBuffered = true;

        // A list has no setting for row height or left margin: a row is as tall as its icon
        // and starts where its icon does. So each icon is drawn inside a larger transparent
        // image that carries the padding.
        var iconSize = ShellIcons.SmallIconSize;
        var scale = iconSize.Width / (float)unscaledIconSize;
        var height = Math.Max((int)(rowHeight * scale), iconSize.Height);
        iconOffset = new((int)(paddingBeforeIcon * scale), (height - iconSize.Height) / 2);
        icons.ImageSize = new(iconOffset.X + iconSize.Width + (int)(paddingAfterIcon * scale), height);

        // Created now so that each image is copied in as it is added. Until an image list has
        // its handle it only keeps a reference to what it is given, and PaddedIcon disposes
        // its bitmap straight after adding it.
        _ = icons.Handle;
        SmallImageList = icons;
    }

    /// <summary>
    /// The index to give a list item for an icon from the system image list.
    /// </summary>
    public int PaddedIcon(int systemIcon)
    {
        if (paddedIcons.TryGetValue(systemIcon, out var index))
        {
            return index;
        }

        using var image = ShellIcons.Render(systemIcon, icons.ImageSize, iconOffset);
        icons.Images.Add(image);
        index = icons.Images.Count - 1;
        paddedIcons[systemIcon] = index;
        return index;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        SetWindowTheme(Handle, "Explorer", null);

        // The arrow lives in the native header, which is new whenever the window is recreated.
        if (sortArrowColumn >= 0)
        {
            SetSortArrow(sortArrowColumn, sortArrowAscending);
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WindowMessageContextMenu && message.WParam == Handle)
        {
            var packed = unchecked((int)message.LParam.ToInt64());
            var fromKeyboard = packed == -1;
            var location = fromKeyboard
                ? Point.Empty
                : new((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            ContextMenuRequested?.Invoke(location, fromKeyboard);
            return;
        }

        // The header passes its own right-clicks up to the list.
        if (message.Msg == WindowMessageContextMenu && message.WParam == Header)
        {
            var packed = unchecked((int)message.LParam.ToInt64());
            var location = packed == -1
                ? PointToScreen(Point.Empty)
                : new((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            HeaderContextMenuRequested?.Invoke(location);
            return;
        }

        try
        {
            base.WndProc(ref message);
        }
        catch (InvalidOperationException) when (!IsHandleCreated)
        {
            // While the window is being torn down, Windows still sends the list notifications
            // (selection changes as items go), and ListView answers some by posting work back
            // with BeginInvoke, which throws once WinForms counts the handle as gone. There is
            // nothing left to update, so the notification is dropped.
        }
    }

    public void SelectAll()
    {
        var state = new ListViewItemState
        {
            State = ListViewItemSelected,
            StateMask = ListViewItemSelected
        };
        SendMessage(Handle, ListViewSetItemState, -1, ref state);
    }

    IntPtr Header => SendMessage(Handle, ListViewGetHeader, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// The index of the column whose header is under a point on the screen, or -1.
    /// </summary>
    public int HeaderColumnAt(Point screen)
    {
        var header = Header;
        var point = new ScreenPoint(screen.X, screen.Y);
        ScreenToClient(header, ref point);
        for (var index = 0; index < Columns.Count; index++)
        {
            var rect = new NativeRect();
            SendMessage(header, HeaderGetItemRect, index, ref rect);
            if (point.X >= rect.Left && point.X < rect.Right)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Widens or narrows a column to fit both its header and the widest of its cells, as
    /// Explorer's Size Column to Fit does.
    /// </summary>
    public void SizeColumnToFit(int column)
    {
        AutoResizeColumn(column, ColumnHeaderAutoResizeStyle.ColumnContent);
        // Measured rather than asked of the list: sizing the last column to its header makes it
        // fill the rest of the list.
        var header = TextRenderer.MeasureText(Columns[column].Text, Font).Width + LogicalToDeviceUnits(24);
        Columns[column].Width = Math.Max(Columns[column].Width, header);
    }

    public void SetSortArrow(int column, bool ascending)
    {
        sortArrowColumn = column;
        sortArrowAscending = ascending;
        var header = Header;
        for (var index = 0; index < Columns.Count; index++)
        {
            var item = new HeaderItem { Mask = HeaderItemFormat };
            SendMessage(header, HeaderGetItem, index, ref item);
            item.Format &= ~(HeaderFormatSortUp | HeaderFormatSortDown);
            if (index == column)
            {
                item.Format |= ascending ? HeaderFormatSortUp : HeaderFormatSortDown;
            }

            SendMessage(header, HeaderSetItem, index, ref item);
        }
    }

    public void SelectEditText(int length) =>
        BeginInvoke(() =>
        {
            var edit = SendMessage(Handle, ListViewGetEditControl, IntPtr.Zero, IntPtr.Zero);
            if (edit != IntPtr.Zero)
            {
                SendMessage(edit, EditSetSelection, IntPtr.Zero, length);
            }
        });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
