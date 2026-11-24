using System.Drawing.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComFormat = System.Runtime.InteropServices.ComTypes.FORMATETC;
using ComMedium = System.Runtime.InteropServices.ComTypes.STGMEDIUM;

/// <summary>
/// What a drag shows, as Explorer's drags show it: a picture of what is dragged, which the shell
/// frames and carries with the pointer over every window that shows drags, Explorer's and Lode's
/// own, and beside it what a drop there would do, as the window under it describes it.
/// </summary>
static partial class DragImage
{
    // The picture's side at 96 dots per inch, and how far above its bottom the pointer is, as
    // the shell has them for a picture of its own.
    const int side = 96;
    const int pointerAboveBottom = 7;

    /// <summary>
    /// Gives a drag a picture of what it carries: the icon of the one item, or of the first
    /// three stacked, the first in front, with how many there are.
    /// </summary>
    public static void Set(DataObject data, IReadOnlyList<(int Icon, int Overlay)> icons, int count, int dpi)
    {
        using var picture = Picture(icons, count, dpi);
        var image = new ShellDragImage
        {
            Size = picture.Size,
            Offset = new(picture.Width / 2, picture.Height - pointerAboveBottom * dpi / 96),
            Bitmap = Premultiplied(picture),
            ColorKey = noColorKey
        };
        if (!GiveToHelper(ref image, data))
        {
            // No drag helper: the drag goes on without a picture.
            DeleteObject(image.Bitmap);
            return;
        }

        // The helper has the bitmap now, and deletes it with the drag. Then as Explorer's drags
        // tell the windows they go over: in the shell's drag loop, with a picture framed as the
        // shell frames its own, and room beside it for what a drop would do.
        SetFlag(data, "InShellDragLoop");
        SetFlag(data, "UsingDefaultDragImage");
        SetFlag(data, "IsShowingText");
    }

    // Through the helper's own pointer, as WinForms calls it, not a runtime wrapper: there is
    // one helper in the process, and the runtime would give every thread asking for it the same
    // wrapper, which one thread ending takes from the others.
    static unsafe bool GiveToHelper(ref ShellDragImage image, DataObject data)
    {
        if (CoCreateInstance(dragDropHelper, IntPtr.Zero, inProcessServer, dragSourceHelper2, out var helper) < 0)
        {
            return false;
        }

        var dataPointer = Marshal.GetComInterfaceForObject<DataObject, ComDataObject>(data);
        try
        {
            // IDragSourceHelper2, after IUnknown's three: InitializeFromBitmap, InitializeFromWindow,
            // SetFlags.
            var methods = *(IntPtr**) helper;
            var initializeFromBitmap = (delegate* unmanaged[Stdcall]<IntPtr, ShellDragImage*, IntPtr, int>) methods[3];
            var setFlags = (delegate* unmanaged[Stdcall]<IntPtr, int, int>) methods[5];
            if (setFlags(helper, allowDropDescriptionText) < 0)
            {
                return false;
            }

            fixed (ShellDragImage* pointer = &image)
            {
                return initializeFromBitmap(helper, pointer, dataPointer) >= 0;
            }
        }
        finally
        {
            Marshal.Release(dataPointer);
            Marshal.Release(helper);
        }
    }

    /// <summary>
    /// The picture a drag of these shows, before the shell frames it.
    /// </summary>
    internal static Bitmap Picture(IReadOnlyList<(int Icon, int Overlay)> icons, int count, int dpi)
    {
        var size = side * dpi / 96;
        var picture = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(picture);
        if (count == 1)
        {
            Draw(graphics, icons[0], Point.Empty, size);
            return picture;
        }

        // A quarter smaller, those behind up and to the right of the first.
        var iconSize = size * 3 / 4;
        var step = (size - iconSize) / 2;
        for (var index = Math.Min(icons.Count, 3) - 1; index >= 0; index--)
        {
            Draw(graphics, icons[index], new(step * index, size - iconSize - step * index), iconSize);
        }

        DrawCount(graphics, count, size, dpi);
        return picture;
    }

    static void Draw(Graphics graphics, (int Icon, int Overlay) icon, Point location, int size)
    {
        using var drawn = ShellIcons.Render(icon.Icon, new(size, size), Point.Empty, icon.Overlay, size);
        graphics.DrawImage(drawn, location);
    }

    // How many, white on the accent colour, at the bottom right.
    static void DrawCount(Graphics graphics, int count, int size, int dpi)
    {
        var text = count.ToString(CultureInfo.CurrentCulture);
        using var font = new Font(Control.DefaultFont.FontFamily, 12f * dpi / 96, FontStyle.Bold, GraphicsUnit.Pixel);
        var textSize = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        var padding = 4 * dpi / 96;
        var height = textSize.Height + padding;
        var width = Math.Max(height, textSize.Width + padding * 2);
        var bounds = new Rectangle(size - width, size - height, width, height);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = ExplorerStyle.RoundedRectangle(bounds, height / 2);
        using var fill = new SolidBrush(ExplorerStyle.Accent);
        graphics.FillPath(fill, shape);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(text, font, Brushes.White, bounds, format);
    }

    /// <summary>
    /// Drags what is given from a control until it is dropped. While the shell shows the
    /// picture, with what a drop would do beside it, the pointer is the plain arrow, as in
    /// Explorer; over a window that doesn't show drags, it is the effect's own.
    /// </summary>
    public static DragDropEffects Drag(Control source, DataObject data, DragDropEffects allowed)
    {
        void Feedback(object? sender, GiveFeedbackEventArgs args)
        {
            if (Flag(data, "IsShowingLayered"))
            {
                args.UseDefaultCursors = false;
                Cursor.Current = Cursors.Default;
            }
        }

        source.GiveFeedback += Feedback;
        try
        {
            return source.DoDragDrop(data, allowed);
        }
        finally
        {
            source.GiveFeedback -= Feedback;
        }
    }

    /// <summary>
    /// The size of the picture a drag has been given, or none. For tests.
    /// </summary>
    internal static Size? SizeOf(DataObject data)
    {
        if (Read(data, "DragImageBits") is not { Length: >= 8 } bits)
        {
            return null;
        }

        return new(BitConverter.ToInt32(bits, 0), BitConverter.ToInt32(bits, 4));
    }

    /// <summary>
    /// Says beside a drag's picture what a drop with the effect given would do to the folder
    /// named, in Explorer's words: copy, move, or make a shortcut in it. Nothing where it can't
    /// be dropped.
    /// </summary>
    public static void Describe(DragEventArgs args, DragDropEffects effect, string? folder)
    {
        switch (effect)
        {
            case DragDropEffects.Copy:
                Describe(args, DropImageType.Copy, "Copy to %1", folder);
                break;
            case DragDropEffects.Move:
                Describe(args, DropImageType.Move, "Move to %1", folder);
                break;
            case DragDropEffects.Link:
                Describe(args, DropImageType.Link, "Create link in %1", folder);
                break;
            default:
                Describe(args, DropImageType.None, "", null);
                break;
        }
    }

    /// <summary>
    /// Says beside a drag's picture what a drop would do. %1 in the message is drawn as the
    /// name it stands for, as the shell draws a place's name.
    /// </summary>
    public static void Describe(DragEventArgs args, DropImageType type, string message, string? name)
    {
        args.DropImageType = type;
        args.Message = message;
        args.MessageReplacementToken = name ?? "";
    }

    static void SetFlag(DataObject data, string format)
    {
        var memory = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(memory, 1);
        var formatEtc = Format(format);
        var medium = new ComMedium
        {
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
            unionmember = memory
        };
        ((ComDataObject) data).SetData(ref formatEtc, ref medium, true);
    }

    static bool Flag(DataObject data, string format) =>
        Read(data, format) is { Length: >= sizeof(int) } bytes &&
        BitConverter.ToInt32(bytes) != 0;

    // A format the shell keeps in the data object, as a copy of its bytes.
    static byte[]? Read(DataObject data, string format)
    {
        var formatEtc = Format(format);
        ComMedium medium;
        try
        {
            ((ComDataObject) data).GetData(ref formatEtc, out medium);
        }
        catch (COMException)
        {
            return null;
        }

        try
        {
            if (medium.unionmember == IntPtr.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bytes = new byte[(int) GlobalSize(medium.unionmember)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    static ComFormat Format(string format) =>
        new()
        {
            cfFormat = unchecked((short) RegisterClipboardFormat(format)),
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
        };

    // A 32 bit bitmap of the picture, its colours premultiplied by its alpha: the shell takes
    // a picture that is see-through in places only so. WinForms' own hands it over opaque,
    // what is see-through filled grey.
    static IntPtr Premultiplied(Bitmap picture)
    {
        var header = new BitmapHeader
        {
            Size = Marshal.SizeOf<BitmapHeader>(),
            Width = picture.Width,
            // Negative, for the rows top first, as the picture has them.
            Height = -picture.Height,
            Planes = 1,
            BitCount = 32
        };
        var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        var locked = picture.LockBits(new(Point.Empty, picture.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[picture.Width * 4];
            for (var y = 0; y < picture.Height; y++)
            {
                Marshal.Copy(locked.Scan0 + y * locked.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, bits + y * row.Length, row.Length);
            }
        }
        finally
        {
            picture.UnlockBits(locked);
        }

        return bitmap;
    }

    // DSH_ALLOWDROPDESCRIPTIONTEXT, and CLR_NONE: no colour taken as see-through.
    const int allowDropDescriptionText = 1;
    const int noColorKey = unchecked((int) 0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential)]
    struct ShellDragImage
    {
        public Size Size;
        public Point Offset;
        public IntPtr Bitmap;
        public int ColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    // CLSID_DragDropHelper, IID_IDragSourceHelper2, and CLSCTX_INPROC_SERVER.
    static Guid dragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    static Guid dragSourceHelper2 = new("83E07D0D-0C5F-4163-BF1A-60B274051E40");
    const uint inProcessServer = 1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string format);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(IntPtr memory);

    // Not LibraryImport, which can't hand over STGMEDIUM's object.
    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref ComMedium medium);
}
