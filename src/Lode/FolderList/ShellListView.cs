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
    /// <para>
    /// A program, an icon file or a shortcut each has an icon of its own, so a folder of
    /// thousands of them, scrolled through, would have every one held for as long as Lode runs,
    /// some 15 KB each at 200%. So the sets hold so many at most, and past that the places of
    /// those used least lately are given to new ones. An image list can't let go of an image
    /// without moving every one after it to another index, so each keeps its place, and the rows
    /// that showed an icon given up are made again, as <see cref="Generation"/> tells. A set
    /// draws each icon as a list on its scale first asks for it, as it draws a row, rather than
    /// every icon there is as it is made, which on the first move to another monitor was every
    /// icon seen so far, at once.
    /// </para>
    /// </summary>
    internal sealed class PaddedIcons : IDisposable
    {
        /// <summary>
        /// How many icons the lists hold at most: about 7 MB of images at 100% scale and 30 MB at
        /// 200%, which is many times what every tab shows at once.
        /// </summary>
        public const int MostIcons = 2048;

        // How many places the sets have to begin with, doubled as they fill.
        const int firstCapacity = 64;

        static int systemDpi = (int)GetDpiForSystem();

        /// <summary>
        /// The icons every list shows.
        /// </summary>
        public static PaddedIcons Shared { get; } = new(MostIcons);

        // Tests make lists on more than one UI thread; the app has one.
        Lock gate = new();
        int most;
        Func<int, Size, Point, int, int, Bitmap> render;

        // What is at each place, null where nothing is, and when it was last asked for or drawn,
        // by a count that goes up with each.
        List<(int Icon, int Overlay)?> places = [];
        List<long> used = [];
        Stack<int> free = [];
        Dictionary<(int Icon, int Overlay), int> indexes = [];
        Dictionary<int, Set> sets = [];
        long uses;
        int capacity;
        int generation;

        /// <param name="most">How many icons are held at most.</param>
        /// <param name="render">Draws an icon of the system image list, with an overlay, at a size,
        /// onto a transparent canvas at an offset, as <see cref="ShellIcons.Render(int, Size, Point,
        /// int, int)"/> does, which it is unless a test gives another.</param>
        public PaddedIcons(int most, Func<int, Size, Point, int, int, Bitmap>? render = null)
        {
            this.most = most;
            this.render = render ?? ShellIcons.Render;
        }

        /// <summary>
        /// Goes up each time icons are let go of, their places given to others: a row made
        /// before then may show another icon than its own, so is made again.
        /// </summary>
        public int Generation => Volatile.Read(ref generation);

        /// <summary>
        /// How many icons are held. For tests.
        /// </summary>
        internal int Count
        {
            get
            {
                lock (gate)
                {
                    return indexes.Count;
                }
            }
        }

        /// <summary>
        /// The images at a scale, as a list shows them.
        /// </summary>
        public sealed class Set
        {
            PaddedIcons owner;
            ImageList images = new() { ColorDepth = ColorDepth.Depth32Bit };
            Point offset;
            int iconSize;
            List<bool> drawn = [];

            internal Set(PaddedIcons owner, int dpi, int capacity)
            {
                this.owner = owner;

                // The system image list's small icons are for the scale Windows started at.
                var small = ShellIcons.SmallIconSize;
                iconSize = small.Width * dpi / systemDpi;
                var scale = iconSize / (float)unscaledIconSize;
                var height = Math.Max((int)(rowHeight * scale), iconSize);
                offset = new((int)(paddingBeforeIcon * scale), (height - iconSize) / 2);
                images.ImageSize = new(offset.X + iconSize + (int)(paddingAfterIcon * scale), height);

                // Created now so that each image is copied in as it is put in. Until an image
                // list has its handle it only keeps a reference to what it is given.
                Handle = images.Handle;
                Grow(capacity);
            }

            public IntPtr Handle { get; }

            /// <summary>
            /// How many icons are drawn in the set. For tests.
            /// </summary>
            internal int Drawn
            {
                get
                {
                    lock (owner.gate)
                    {
                        return drawn.Count(_ => _);
                    }
                }
            }

            /// <summary>
            /// The list is about to draw the image at an index: its icon is drawn there first,
            /// unless it is already.
            /// </summary>
            public void Show(int index) =>
                owner.Show(this, index);

            // Under the gate, from here on. Places made, blank, for the icons to come.
            internal void Grow(int count)
            {
                if (count <= drawn.Count)
                {
                    return;
                }

                ImageList_SetImageCount(Handle, (uint)count);
                drawn.AddRange(Enumerable.Repeat(false, count - drawn.Count));
            }

            internal bool IsDrawn(int index) =>
                drawn[index];

            internal void Forget(int index) =>
                drawn[index] = false;

            internal void Draw(int index, (int Icon, int Overlay) icon)
            {
                using var image = owner.render(icon.Icon, images.ImageSize, offset, icon.Overlay, iconSize);
                images.Images[index] = image;
                drawn[index] = true;
            }

            internal void Dispose() =>
                images.Dispose();
        }

        /// <summary>
        /// The set of images for a monitor at <paramref name="dpi"/>, made the first time with
        /// every icon's place, none of them drawn.
        /// </summary>
        public Set For(int dpi)
        {
            lock (gate)
            {
                if (!sets.TryGetValue(dpi, out var set))
                {
                    set = new(this, dpi, capacity);
                    sets[dpi] = set;
                }

                return set;
            }
        }

        /// <summary>
        /// The index, in every set, of an icon from the system image list with an overlay drawn
        /// over it, given a place the first time.
        /// </summary>
        public int Index(int systemIcon, int overlay)
        {
            lock (gate)
            {
                var key = (systemIcon, overlay);
                if (indexes.TryGetValue(key, out var index))
                {
                    used[index] = ++uses;
                    return index;
                }

                if (free.Count == 0 &&
                    places.Count >= most)
                {
                    LetGoOfLeastUsed();
                }

                if (free.TryPop(out index))
                {
                    places[index] = key;
                    used[index] = ++uses;
                }
                else
                {
                    index = places.Count;
                    places.Add(key);
                    used.Add(++uses);
                    if (places.Count > capacity)
                    {
                        capacity = Math.Min(most, Math.Max(firstCapacity, capacity * 2));
                        foreach (var set in sets.Values)
                        {
                            set.Grow(capacity);
                        }
                    }
                }

                indexes[key] = index;
                return index;
            }
        }

        // A quarter of the places, those used least lately, given up together, so the rows are
        // made again once for many new icons rather than for each.
        void LetGoOfLeastUsed()
        {
            var leastUsed = Enumerable.Range(0, places.Count)
                .OrderBy(_ => used[_])
                .Take(Math.Max(1, most / 4));
            foreach (var index in leastUsed)
            {
                indexes.Remove(places[index]!.Value);
                places[index] = null;
                free.Push(index);
                foreach (var set in sets.Values)
                {
                    set.Forget(index);
                }
            }

            Interlocked.Increment(ref generation);
        }

        void Show(Set set, int index)
        {
            lock (gate)
            {
                if (index < 0 ||
                    index >= places.Count ||
                    places[index] is not { } icon)
                {
                    return;
                }

                used[index] = ++uses;
                if (!set.IsDrawn(index))
                {
                    set.Draw(index, icon);
                }
            }
        }

        // The shared icons last as long as Lode; a test's or a benchmark's own are let go of.
        public void Dispose()
        {
            lock (gate)
            {
                foreach (var set in sets.Values)
                {
                    set.Dispose();
                }

                sets.Clear();
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

    // WM_MOUSEWHEEL, and MK_CONTROL among the keys it says were held.
    const int mouseWheel = 0x020A;
    const int wheelControl = 0x0008;

    // WM_MBUTTONDOWN, WM_MBUTTONUP and WM_MBUTTONDBLCLK.
    const int middleButtonDown = 0x0207;
    const int middleButtonUp = 0x0208;
    const int middleButtonDoubleClick = 0x0209;

    // The row the middle button went down on, -1 for none.
    int middlePressed = -1;

    /// <summary>
    /// A row was clicked with the middle button, by its index: the button went down and came up
    /// over it. Each click, a second soon after the first too, which comes as a double click.
    /// </summary>
    public event Action<int>? MiddleClicked;

    /// <summary>
    /// The mouse wheel was turned over the list with Ctrl held, by this much, in 120ths of a
    /// notch: away from the user for bigger icons, as in Explorer, toward for smaller. The list
    /// doesn't scroll for it.
    /// </summary>
    public event Action<int>? ZoomRequested;

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
    static int notificationCodeOffset = 2 * IntPtr.Size;
    static int headerNotificationItemOffset = 3 * IntPtr.Size;
    static int headerNotificationHeaderItemOffset = 3 * IntPtr.Size + 2 * sizeof(int);
    const int headerItemWidthOffset = sizeof(uint);

    public ShellListView() =>
        DoubleBuffered = true;

    /// <summary>
    /// The index to give a list item for an icon from the system image list, with an overlay
    /// drawn over it, as <see cref="ShellIcons.GetOverlay"/> gives it, or none.
    /// </summary>
    public static int PaddedIcon(int systemIcon, int overlay = 0) =>
        PaddedIcons.Shared.Index(systemIcon, overlay);

    /// <summary>
    /// The size of the image a row shows on a monitor at <paramref name="dpi"/>, the icon with
    /// its padding, as the lists' shared image list has it. Empty if that list is gone. For
    /// tests.
    /// </summary>
    internal static Size PaddedIconSize(int dpi)
    {
        ImageList_GetIconSize(PaddedIcons.Shared.For(dpi).Handle, out var width, out var height);
        return new(width, height);
    }

    PaddedIcons icons = PaddedIcons.Shared;

    // The set the rows show, at the list's scale, once it has a window.
    PaddedIcons.Set? rowImages;

    /// <summary>
    /// The padded icons the rows show, whose indexes a row is given: those every list shares,
    /// unless a test gives the list its own.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal PaddedIcons Icons
    {
        get => icons;
        set
        {
            icons = value;
            UseImagesFor(DeviceDpi);
        }
    }

    /// <summary>
    /// The image list the list's rows are showing. For tests.
    /// </summary>
    internal IntPtr RowImages =>
        SendMessage(Handle, ListViewGetImageList, ListViewImageListSmall, IntPtr.Zero);

    // Moved to a monitor at another scale: the rows' images, and so their height, for it.
    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        UseImagesFor(DeviceDpi);
    }

    internal void UseImagesFor(int dpi)
    {
        if (IsHandleCreated)
        {
            rowImages = icons.For(dpi);
            SendMessage(Handle, ListViewSetImageList, ListViewImageListSmall, rowImages.Handle);
        }
    }

    // The list asks for each row's image as it draws it, as it does its text, so the icon at the
    // index it is given is drawn into the set it shows, if it isn't yet, before it is drawn from.
    // Large icons draws from the thumbnails instead.
    void DrawRowImage(IntPtr notification)
    {
        if (rowImages != null &&
            View != View.LargeIcon &&
            View != View.Tile &&
            (Marshal.ReadInt32(notification, itemOffset) & listViewItemImage) != 0)
        {
            rowImages.Show(Marshal.ReadInt32(notification, imageOffset));
        }
    }

    IntPtr largeImages;

    /// <summary>
    /// The image list Large icons draws from, given to the window itself as the rows' is, which
    /// leaves it alone when it closes. Zero for none.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IntPtr LargeImages
    {
        get => largeImages;
        set
        {
            largeImages = value;
            if (IsHandleCreated)
            {
                SendMessage(Handle, ListViewSetImageList, ListViewImageListNormal, value);
            }
        }
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        // Given to the window itself rather than through SmallImageList, which would have the
        // shared list hold on to every list that ever showed it. Before the rows go in, which
        // take their height from it. Windows leaves the list alone when this window closes, as
        // every ListView asks it to.
        rowImages = icons.For(DeviceDpi);
        SendMessage(Handle, ListViewSetImageList, ListViewImageListSmall, rowImages.Handle);
        if (largeImages != IntPtr.Zero)
        {
            SendMessage(Handle, ListViewSetImageList, ListViewImageListNormal, largeImages);
        }

        // Each row's cut state, and whether a drop would go into it, are asked for as it is
        // drawn, as the rows are.
        SendMessage(Handle, listViewSetCallbackMask, listViewItemCut | listViewItemDropHighlighted, IntPtr.Zero);
        base.OnHandleCreated(args);
        BackColor = ExplorerStyle.Window;
        SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);

        // The arrow lives in the native header, which is new whenever the window is recreated.
        if (sortArrowColumn >= 0)
        {
            SetSortArrow(sortArrowColumn, sortArrowAscending);
        }
    }

    // LVN_GETDISPINFOW, and in its LVITEM the state flag, the cut state and the drop highlight.
    const int listViewGetDisplayInfo = -177;
    const int listViewItemState = 0x8;
    const int listViewItemCut = 0x4;
    const int listViewItemDropHighlighted = 0x8;
    const int listViewSetCallbackMask = 0x100B;

    // NMLVDISPINFO is NMHDR, then LVITEM: mask, item, subitem, state, state mask. LVITEM holds
    // pointers, so starts on a pointer's boundary.
    static int itemOffset = IntPtr.Size == 8 ? 24 : 12;

    // LVIF_IMAGE, and the image's index in LVITEM, after the text and its length.
    const int listViewItemImage = 0x2;
    static int imageOffset = itemOffset + (IntPtr.Size == 8 ? 36 : 28);

    /// <summary>
    /// Whether the row at an index is of an item cut to the clipboard, which the list draws
    /// dimmed, its icon faded, as Explorer does. Asked as each row is drawn.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, bool>? IsCut { get; set; }

    /// <summary>
    /// A short mark at the right of a row's name, in a colour of its own: a changed file's
    /// letter, say. Null for none. Asked as each row is drawn.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, NameMark?>? NameMarkOf { get; set; }

    /// <summary>
    /// A mark after a row's name: the name, to see whether it leaves room, and what the mark
    /// says, in what colour.
    /// </summary>
    public sealed record NameMark(string Name, string Text, Color Color);

    // NM_CUSTOMDRAW, as the list draws: before anything, asked to be told of each row; before a
    // row, asked to be told once it is drawn; and after, to draw over it. NMCUSTOMDRAW is NMHDR,
    // then the stage, the device context, the row's bounds and its index, each pointer on a
    // pointer's boundary.
    const int customDraw = -12;
    const int drawPrePaint = 0x1;
    const int drawItemPrePaint = 0x10001;
    const int drawItemPostPaint = 0x10002;
    const int notifyItemDraw = 0x20;
    const int notifyPostPaint = 0x10;
    static int drawStageOffset = IntPtr.Size == 8 ? 24 : 12;
    static int drawContextOffset = IntPtr.Size == 8 ? 32 : 16;
    static int drawItemOffset = IntPtr.Size == 8 ? 56 : 36;

    // Pixels at 100% scale: how far in from the column's edge the mark ends, how far the list
    // draws a name in from where its label starts, and the least gap between the two.
    const int markInset = 8;
    const int nameIndent = 6;
    const int markGap = 8;

    // The mark at the right of the name's cell, drawn over the row once the list has drawn it,
    // so over its highlight too. Only where the whole name leaves room: one cut short is left
    // with its colour alone, rather than drawn over.
    void DrawNameMark(int index, IntPtr context)
    {
        if (index < 0 ||
            index >= VirtualListSize ||
            NameMarkOf?.Invoke(index) is not { } mark)
        {
            return;
        }

        if (View == View.LargeIcon)
        {
            DrawBadge(index, context, mark);
            return;
        }

        var label = GetItemRect(index, ItemBoundsPortion.Label);
        using var graphics = Graphics.FromHdc(context);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
        var markWidth = TextRenderer.MeasureText(graphics, mark.Text, Font, Size.Empty, flags).Width;
        var nameWidth = TextRenderer.MeasureText(graphics, mark.Name, Font, Size.Empty, flags).Width;
        var left = label.Right - LogicalToDeviceUnits(markInset) - markWidth;
        if (label.Left + LogicalToDeviceUnits(nameIndent) + nameWidth + LogicalToDeviceUnits(markGap) > left)
        {
            return;
        }

        TextRenderer.DrawText(graphics, mark.Text, Font, new Rectangle(left, label.Top, markWidth, label.Height), mark.Color, flags);
    }

    // Pixels at 100% scale: the space either side of a mark in its box in Large icons.
    const int badgePadding = 3;

    // In Large icons a name lies under its icon, wrapped, with no room after it. So the mark is
    // at the foot of the item's picture, on its right, in a box of the list's own colour edged
    // in the mark's, to read over whatever the picture is.
    void DrawBadge(int index, IntPtr context, NameMark mark)
    {
        if (largeImages == IntPtr.Zero)
        {
            return;
        }

        // The picture lies across the middle of the icon's part of the item.
        var icon = GetItemRect(index, ItemBoundsPortion.Icon);
        ImageList_GetIconSize(largeImages, out var imageWidth, out var imageHeight);
        using var graphics = Graphics.FromHdc(context);

        // Measured without centring, which has Windows give half the height.
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var text = TextRenderer.MeasureText(graphics, mark.Text, Font, Size.Empty, flags);
        var padding = LogicalToDeviceUnits(badgePadding);
        var right = icon.Left + (icon.Width + imageWidth) / 2;
        var bottom = icon.Top + (icon.Height + imageHeight) / 2;
        var badge = Rectangle.FromLTRB(right - text.Width - 2 * padding, bottom - text.Height, right, bottom);
        using var background = new SolidBrush(BackColor);
        using var edge = new Pen(mark.Color);
        graphics.FillRectangle(background, badge);
        graphics.DrawRectangle(edge, badge with
        {
            Width = badge.Width - 1,
            Height = badge.Height - 1
        });
        TextRenderer.DrawText(graphics, mark.Text, Font, badge, mark.Color, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    int dropTarget = -1;

    /// <summary>
    /// The row of the folder what is being dragged would be dropped into, drawn highlighted, as
    /// Explorer draws it; -1 for none.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int DropTarget
    {
        get => dropTarget;
        set
        {
            if (value == dropTarget)
            {
                return;
            }

            var was = dropTarget;
            dropTarget = value;
            Redraw(was);
            Redraw(value);
        }
    }

    void Redraw(int index)
    {
        if (IsHandleCreated &&
            index >= 0 &&
            index < VirtualListSize)
        {
            RedrawItems(index, index, false);
        }
    }

    /// <summary>
    /// Whether the list draws the row at <paramref name="index"/> as cut, as it would ask. For
    /// tests.
    /// </summary>
    internal bool DrawsCut(int index) =>
        Draws(index, listViewItemCut);

    /// <summary>
    /// Whether the list draws the row at <paramref name="index"/> as where a drop would go, as
    /// it would ask. For tests.
    /// </summary>
    internal bool DrawsDropTarget(int index) =>
        Draws(index, listViewItemDropHighlighted);

    bool Draws(int index, int flag)
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
            Marshal.WriteInt32(notification, itemOffset + 16, flag);
            AnswerState(notification);
            return (Marshal.ReadInt32(notification, itemOffset + 12) & flag) != 0;
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

    // The list asks for the cut state of each row, and its drop highlight, as it draws it, as
    // it was told to when made. Whether it was asked whether the row is cut.
    bool AnswerState(IntPtr notification)
    {
        var mask = Marshal.ReadInt32(notification, itemOffset);
        var index = Marshal.ReadInt32(notification, itemOffset + 4);
        var stateMask = Marshal.ReadInt32(notification, itemOffset + 16);
        if ((mask & listViewItemState) == 0)
        {
            return false;
        }

        var state = Marshal.ReadInt32(notification, itemOffset + 12);
        if ((stateMask & listViewItemDropHighlighted) != 0)
        {
            state &= ~listViewItemDropHighlighted;
            if (index >= 0 &&
                index == dropTarget)
            {
                state |= listViewItemDropHighlighted;
            }
        }

        var askedCut = (stateMask & listViewItemCut) != 0 &&
                       IsCut != null;
        if (askedCut)
        {
            state &= ~listViewItemCut;
            if (index >= 0 &&
                IsCut!(index))
            {
                state |= listViewItemCut;
            }
        }

        Marshal.WriteInt32(notification, itemOffset + 12, state);
        return askedCut;
    }

    protected override void WndProc(ref Message message)
    {
        // Drawn as Windows Forms draws it, the items' colours with it, then asked to be told
        // once each row is drawn, to draw its mark over it.
        if (message.Msg == reflectedNotify &&
            NameMarkOf != null &&
            Marshal.ReadInt32(message.LParam, notificationCodeOffset) == customDraw)
        {
            var stage = Marshal.ReadInt32(message.LParam, drawStageOffset);
            if (stage == drawItemPostPaint)
            {
                DrawNameMark((int) Marshal.ReadIntPtr(message.LParam, drawItemOffset), Marshal.ReadIntPtr(message.LParam, drawContextOffset));
                message.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref message);
            if (stage == drawPrePaint)
            {
                message.Result |= notifyItemDraw;
            }

            if (stage == drawItemPrePaint)
            {
                message.Result |= notifyPostPaint;
            }

            return;
        }

        if (message.Msg == reflectedNotify &&
            Marshal.ReadInt32(message.LParam, notificationCodeOffset) == listViewGetDisplayInfo)
        {
            base.WndProc(ref message);
            DrawRowImage(message.LParam);
            if (AnswerState(message.LParam))
            {
                CutAsked++;
            }

            return;
        }

        if (message.Msg is middleButtonDown or middleButtonDoubleClick)
        {
            middlePressed = RowAt(message.LParam);
        }

        if (message.Msg == middleButtonUp)
        {
            var pressed = middlePressed;
            middlePressed = -1;
            if (pressed >= 0 &&
                RowAt(message.LParam) == pressed)
            {
                MiddleClicked?.Invoke(pressed);
            }
        }

        // Ctrl held, as the message says, the wheel changes the view rather than scrolling.
        if (message.Msg == mouseWheel &&
            ZoomRequested != null &&
            ((long) message.WParam & wheelControl) != 0)
        {
            ZoomRequested((short) ((long) message.WParam >> 16));
            return;
        }

        if (message.Msg == WindowMessageContextMenu &&
            message.WParam == Handle)
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
        if (message.Msg == WindowMessageContextMenu &&
            message.WParam == Header)
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

    // The row at the point a mouse message gives, -1 for none.
    int RowAt(IntPtr point)
    {
        var x = (short) ((long) point & 0xFFFF);
        var y = (short) (((long) point >> 16) & 0xFFFF);
        return GetItemAt(x, y)?.Index ?? -1;
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
            if (point.X >= rect.Left &&
                point.X < rect.Right)
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
        int[] widths = Enumerable.Repeat(-1, latinEnd).ToArray();

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

    // WM_SETTEXT.
    const int setText = 0x000C;

    /// <summary>
    /// Puts <paramref name="text"/> in the box an item's name is being edited in, all of it
    /// selected, in place of the name the list shows: a drive's label, rather than its name with
    /// its letter.
    /// </summary>
    public void SetEditText(string text) =>
        BeginInvoke(() =>
        {
            var edit = SendMessage(Handle, ListViewGetEditControl, IntPtr.Zero, IntPtr.Zero);
            if (edit != IntPtr.Zero)
            {
                SendMessage(edit, setText, IntPtr.Zero, text);
                SendMessage(edit, EditSetSelection, IntPtr.Zero, -1);
            }
        });
}
