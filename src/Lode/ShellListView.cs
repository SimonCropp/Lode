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
    const int printMessage = 0x0317;
    const int printNonClient = 0x0002;
    const int printClient = 0x0004;
    const int printBackground = 0x0008;

    /// <summary>
    /// A list has no setting for row height or left margin: a row is as tall as its icon and
    /// starts where its icon does. So each icon is drawn inside a larger transparent image that
    /// carries the padding. Every list on a monitor of a scale shows the same icons at the same
    /// size, so they share one set of these, rather than each tab keeping a copy of every icon it
    /// has shown. A set for each scale a list has been on holds the same icons at the same
    /// indexes, so a list moved to another monitor keeps its rows' images, only bigger or smaller.
    /// </summary>
    static class PaddedIcons
    {
        static readonly Lock gate = new();
        static readonly List<(int Icon, int Overlay)> icons = [];
        static readonly Dictionary<(int Icon, int Overlay), int> indexes = [];
        static readonly Dictionary<int, Set> sets = [];
        static readonly int systemDpi = (int)GetDpiForSystem();

        sealed class Set
        {
            readonly ImageList images = new() { ColorDepth = ColorDepth.Depth32Bit };
            readonly Point offset;
            readonly int iconSize;

            public Set(int dpi)
            {
                // The system image list's small icons are for the scale Windows started at.
                var small = ShellIcons.SmallIconSize;
                iconSize = small.Width * dpi / systemDpi;
                var scale = iconSize / (float)unscaledIconSize;
                var height = Math.Max((int)(rowHeight * scale), iconSize);
                offset = new((int)(paddingBeforeIcon * scale), (height - iconSize) / 2);
                images.ImageSize = new(offset.X + iconSize + (int)(paddingAfterIcon * scale), height);

                // Created now so that each image is copied in as it is added. Until an image
                // list has its handle it only keeps a reference to what it is given, and Add
                // disposes its bitmap straight after adding it.
                Handle = images.Handle;
                foreach (var (icon, overlay) in icons)
                {
                    Add(icon, overlay);
                }
            }

            public IntPtr Handle { get; }

            public void Add(int icon, int overlay)
            {
                using var image = ShellIcons.Render(icon, images.ImageSize, offset, overlay, iconSize);
                images.Images.Add(image);
            }
        }

        // Tests make lists on more than one UI thread; the app has one.
        public static IntPtr Handle(int dpi)
        {
            lock (gate)
            {
                if (!sets.TryGetValue(dpi, out var set))
                {
                    set = new(dpi);
                    sets[dpi] = set;
                }

                return set.Handle;
            }
        }

        public static int Index(int systemIcon, int overlay)
        {
            lock (gate)
            {
                if (indexes.TryGetValue((systemIcon, overlay), out var index))
                {
                    return index;
                }

                foreach (var set in sets.Values)
                {
                    set.Add(systemIcon, overlay);
                }

                index = icons.Count;
                icons.Add((systemIcon, overlay));
                indexes[(systemIcon, overlay)] = index;
                return index;
            }
        }
    }

    int sortArrowColumn = -1;
    bool sortArrowAscending;

    // Asked for by the list while it has no rows, for the text it shows in their place.
    const int reflectedNotify = 0x2000 + WindowMessageNotify;
    const int listViewGetEmptyMarkup = -187;
    const int emptyMarkupCentered = 0x1;
    const int emptyMarkupLength = 2084;
    const int listViewResetEmptyText = 0x1054;

    string? emptyText;

    /// <summary>
    /// What the list says while it has no rows, in the middle of it. Nothing when null.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? EmptyText
    {
        get => emptyText;
        set
        {
            if (emptyText == value)
            {
                return;
            }

            emptyText = value;
            if (!IsHandleCreated)
            {
                return;
            }

            // The list asks for the text once, and keeps it until told to ask again.
            SendMessage(Handle, listViewResetEmptyText, IntPtr.Zero, IntPtr.Zero);
            Invalidate();
        }
    }

    public event Action<Point, bool>? ContextMenuRequested;

    /// <summary>
    /// The column header was right-clicked, at this point on the screen, or the context menu key
    /// pressed while it had the focus, in which case the point is the header's top left.
    /// </summary>
    public event Action<Point>? HeaderContextMenuRequested;

    /// <summary>
    /// The divider at the right of a column's header was double-clicked, which asks for the
    /// column, by its index, to be sized to fit.
    /// </summary>
    public event Action<int>? ColumnFitRequested;

    /// <summary>
    /// The divider at the right of a column's header was dragged and let go, leaving the column,
    /// by its index, this wide. Raised for the user's sizing only: not for a width set in code,
    /// nor one Windows changes with the screen's scale.
    /// </summary>
    public event Action<int, int>? ColumnResized;

    /// <summary>
    /// The horizontal scrollbar showed or went, or its range, page or position changed, or the
    /// list was moved or resized, which moves it.
    /// </summary>
    public event Action? HorizontalScrollChanged;

    /// <summary>
    /// The range, page and position of a scrollbar, as Windows has them.
    /// </summary>
    internal readonly record struct ScrollState(int Minimum, int Maximum, int Page, int Position);

    // The horizontal scrollbar, and where the list was, when last looked at.
    (ScrollState?, Rectangle, Size) horizontalScroll;

    /// <summary>
    /// A scrollbar lying over the list's own horizontal one, in its place.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Control? HorizontalScrollBarCover { get; set; }

    /// <summary>
    /// The horizontal scrollbar, or null while it does not show.
    /// </summary>
    internal ScrollState? HorizontalScrollBar
    {
        get
        {
            if (!IsHandleCreated ||
                (GetWindowLong(Handle, WindowStyle) & WindowStyleHorizontalScroll) == 0)
            {
                return null;
            }

            var info = new ScrollInfo
            {
                Size = (uint)Marshal.SizeOf<ScrollInfo>(),
                Mask = ScrollInfoRange | ScrollInfoPage | ScrollInfoPosition
            };
            GetScrollInfo(Handle, ScrollBarHorizontal, ref info);
            return new(info.Minimum, info.Maximum, (int)info.Page, info.Position);
        }
    }

    // NMHDR is a window, an id and a code; NMHEADER follows it with the header item's index, the
    // mouse button, and the HDITEM, which starts with its mask and then its width.
    static readonly int notificationCodeOffset = 2 * IntPtr.Size;
    static readonly int headerNotificationItemOffset = 3 * IntPtr.Size;
    static readonly int headerNotificationHeaderItemOffset = 3 * IntPtr.Size + 2 * sizeof(int);
    const int headerItemWidthOffset = sizeof(uint);

    public ShellListView() =>
        DoubleBuffered = true;

    /// <summary>
    /// The index to give a list item for an icon from the system image list, with an overlay
    /// drawn over it, as <see cref="ShellIcons.GetOverlay"/> gives it, or none.
    /// </summary>
    public static int PaddedIcon(int systemIcon, int overlay = 0) =>
        PaddedIcons.Index(systemIcon, overlay);

    /// <summary>
    /// The size of the image a row shows on a monitor at <paramref name="dpi"/>, the icon with
    /// its padding, as the lists' shared image list has it. Empty if that list is gone. For
    /// tests.
    /// </summary>
    internal static Size PaddedIconSize(int dpi)
    {
        ImageList_GetIconSize(PaddedIcons.Handle(dpi), out var width, out var height);
        return new(width, height);
    }

    /// <summary>
    /// The image list the list's rows are showing. For tests.
    /// </summary>
    internal IntPtr RowImages =>
        SendMessage(Handle, ListViewGetImageList, ListViewImageListSmall, IntPtr.Zero);

    // Moved to a monitor at another scale: the rows' images, and so their height, for it.
    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        UseImagesFor(DeviceDpi);
    }

    internal void UseImagesFor(int dpi)
    {
        if (IsHandleCreated)
        {
            SendMessage(Handle, ListViewSetImageList, ListViewImageListSmall, PaddedIcons.Handle(dpi));
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        // Given to the window itself rather than through SmallImageList, which would have the
        // shared list hold on to every list that ever showed it. Before the rows go in, which
        // take their height from it. Windows leaves the list alone when this window closes, as
        // every ListView asks it to.
        SendMessage(Handle, ListViewSetImageList, ListViewImageListSmall, PaddedIcons.Handle(DeviceDpi));

        // Each row's cut state is asked for as it is drawn, as the rows are.
        SendMessage(Handle, listViewSetCallbackMask, listViewItemCut, IntPtr.Zero);
        base.OnHandleCreated(eventArgs);
        BackColor = ExplorerStyle.Window;
        SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);

        // The arrow lives in the native header, which is new whenever the window is recreated.
        if (sortArrowColumn >= 0)
        {
            SetSortArrow(sortArrowColumn, sortArrowAscending);
        }
    }

    // LVN_GETDISPINFOW, and in its LVITEM the state flag and the cut state.
    const int listViewGetDisplayInfo = -177;
    const int listViewItemState = 0x8;
    const int listViewItemCut = 0x4;
    const int listViewSetCallbackMask = 0x100B;

    // NMLVDISPINFO is NMHDR, then LVITEM: mask, item, subitem, state, state mask. LVITEM holds
    // pointers, so starts on a pointer's boundary.
    static readonly int itemOffset = IntPtr.Size == 8 ? 24 : 12;

    /// <summary>
    /// Whether the row at an index is of an item cut to the clipboard, which the list draws
    /// dimmed, its icon faded, as Explorer does. Asked as each row is drawn.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, bool>? IsCut { get; set; }

    /// <summary>
    /// Whether the list draws the row at <paramref name="index"/> as cut, as it would ask. For
    /// tests.
    /// </summary>
    internal bool DrawsCut(int index)
    {
        var size = itemOffset + 20;
        var notification = Marshal.AllocHGlobal(size + 64);
        try
        {
            for (var offset = 0; offset < size + 64; offset++)
            {
                Marshal.WriteByte(notification, offset, 0);
            }

            Marshal.WriteInt32(notification, notificationCodeOffset, listViewGetDisplayInfo);
            Marshal.WriteInt32(notification, itemOffset, listViewItemState);
            Marshal.WriteInt32(notification, itemOffset + 4, index);
            Marshal.WriteInt32(notification, itemOffset + 16, listViewItemCut);
            AnswerCut(notification);
            return (Marshal.ReadInt32(notification, itemOffset + 12) & listViewItemCut) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(notification);
        }
    }

    /// <summary>
    /// How many times the list has asked whether a row is cut, as it draws. For tests.
    /// </summary>
    internal int CutAsked { get; private set; }

    // The list asks for the cut state of each row as it draws it, as it was told to when made.
    // Whether it was asked.
    bool AnswerCut(IntPtr notification)
    {
        var mask = Marshal.ReadInt32(notification, itemOffset);
        var index = Marshal.ReadInt32(notification, itemOffset + 4);
        var stateMask = Marshal.ReadInt32(notification, itemOffset + 16);
        if ((mask & listViewItemState) == 0 ||
            (stateMask & listViewItemCut) == 0 ||
            IsCut is not { } isCut)
        {
            return false;
        }

        var state = Marshal.ReadInt32(notification, itemOffset + 12) & ~listViewItemCut;
        if (index >= 0 &&
            isCut(index))
        {
            state |= listViewItemCut;
        }

        Marshal.WriteInt32(notification, itemOffset + 12, state);
        return true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == reflectedNotify &&
            Marshal.ReadInt32(message.LParam, notificationCodeOffset) == listViewGetDisplayInfo)
        {
            base.WndProc(ref message);
            if (AnswerCut(message.LParam))
            {
                CutAsked++;
            }

            return;
        }

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

        // A double-click on the divider at the right of a column's header sizes the column to
        // fit. Left to do that itself the list goes by the rows it asks for, which are only those
        // on screen, so it is handed on instead.
        if (message.Msg == WindowMessageNotify &&
            ColumnFitRequested != null &&
            Marshal.ReadInt32(message.LParam, notificationCodeOffset) is HeaderDividerDoubleClick or HeaderDividerDoubleClickAnsi &&
            Marshal.ReadIntPtr(message.LParam) == Header)
        {
            ColumnFitRequested(Marshal.ReadInt32(message.LParam, headerNotificationItemOffset));
            return;
        }

        // A divider let go of: its column is as wide as the HDITEM says, or else as the list has
        // it, once the list has had the notification.
        if (message.Msg == WindowMessageNotify &&
            Marshal.ReadInt32(message.LParam, notificationCodeOffset) is HeaderEndTrack or HeaderEndTrackAnsi &&
            Marshal.ReadIntPtr(message.LParam) == Header)
        {
            base.WndProc(ref message);
            var column = Marshal.ReadInt32(message.LParam, headerNotificationItemOffset);
            var item = Marshal.ReadIntPtr(message.LParam, headerNotificationHeaderItemOffset);
            if (column < 0 ||
                column >= Columns.Count)
            {
                return;
            }

            var width = Columns[column].Width;
            if (item != IntPtr.Zero &&
                (Marshal.ReadInt32(item) & HeaderItemWidth) != 0)
            {
                width = Marshal.ReadInt32(item, headerItemWidthOffset);
            }

            ColumnResized?.Invoke(column, width);
            return;
        }

        // NMLVEMPTYMARKUP is NMHDR, then flags, then the text, as markup, which plain text is.
        if (message.Msg == reflectedNotify &&
            Marshal.ReadInt32(message.LParam, notificationCodeOffset) == listViewGetEmptyMarkup)
        {
            if (emptyText == null)
            {
                message.Result = IntPtr.Zero;
                return;
            }

            var flagsOffset = 3 * IntPtr.Size;
            var text = emptyText.ToCharArray(0, Math.Min(emptyText.Length, emptyMarkupLength - 1));
            Marshal.WriteInt32(message.LParam, flagsOffset, emptyMarkupCentered);
            Marshal.Copy(text, 0, message.LParam + flagsOffset + sizeof(int), text.Length);
            Marshal.WriteInt16(message.LParam, flagsOffset + sizeof(int) + text.Length * sizeof(char), 0);
            message.Result = 1;
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

        // DrawToBitmap, which captures a window as an image, prints windows that overlap in the
        // opposite order to how they are stacked, so the list's own horizontal scrollbar prints
        // over the one lying on it, which on screen hides it. So that one is printed again, over
        // the list, where it is.
        if (message.Msg == printMessage &&
            HorizontalScrollBarCover is { Visible: true, IsHandleCreated: true } cover)
        {
            base.WndProc(ref message);
            var dc = message.WParam;
            var saved = SaveDC(dc);
            OffsetViewportOrgEx(dc, cover.Left - Left, cover.Top - Top, IntPtr.Zero);
            SendMessage(cover.Handle, printMessage, dc, printClient | printNonClient | printBackground);
            RestoreDC(dc, saved);
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

        // Whatever showed, moved or resized the horizontal scrollbar did it in answer to a
        // message to the list: a key, the wheel, a column widened, rows put in, the list resized.
        // So it is looked at after each, which costs two quick questions of Windows.
        if (HorizontalScrollChanged != null)
        {
            var state = (HorizontalScrollBar, Bounds, ClientSize);
            if (state != horizontalScroll)
            {
                horizontalScroll = state;
                HorizontalScrollChanged();
            }
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
    /// How wide the list draws some text, in pixels.
    /// </summary>
    public int TextWidth(string text) =>
        (int)SendMessage(Handle, ListViewGetStringWidth, IntPtr.Zero, text);

    /// <summary>
    /// Measures text as <see cref="TextWidth"/> does, for as much of it as a big folder has. The
    /// list takes some 15 microseconds a text, more for text in another script: seconds for a
    /// folder of a hundred thousand names. But the list measures as GDI does, which on screen
    /// neither kerns Latin nor joins its letters, so text of printable Latin, all in the list's
    /// own font, is as wide as its characters, each as wide as it is alone. Such text is measured
    /// a character at a time, each character asked of the list once. Text with anything else in
    /// it, which may be joined up or drawn from another font, is asked of the list whole.
    /// </summary>
    public sealed class TextMeasure(ShellListView list)
    {
        // To the end of Latin Extended-A: accented letters, and the signs Latin-1 has.
        const char latinEnd = (char)0x180;

        // Each character's width, -1 for one not yet asked of the list.
        readonly int[] widths = Enumerable.Repeat(-1, latinEnd).ToArray();

        public int Width(string text)
        {
            if (!IsLatin(text))
            {
                return list.TextWidth(text);
            }

            var width = 0;
            foreach (var character in text)
            {
                ref var known = ref widths[character];
                if (known < 0)
                {
                    known = list.TextWidth(character.ToString());
                }

                width += known;
            }

            return width;
        }

        // Printable Latin, so none of the control characters: those below a space, and those
        // from delete to the no-break space.
        static bool IsLatin(string text)
        {
            foreach (var character in text)
            {
                if (character is < ' ' or >= '\x7F' and < '\xA0' or >= latinEnd)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Widens or narrows a column to fit both its header and the widest of its cells, as
    /// Explorer's Size Column to Fit does. The cells measured are those of the rows the list
    /// asks for, which in a list that holds no rows of its own are only the ones on screen.
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
}
