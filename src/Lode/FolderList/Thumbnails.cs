/// <summary>
/// What Large icons shows of each item: its thumbnail, as Explorer shows one of a picture, a
/// video, or a document the shell can make one of, or else its icon, as big. Each is read through
/// the shell's IShellItemImageFactory off the UI thread, on threads of their own set up for the
/// shell's COM objects, as reading one can take as long as opening the file: only for the items
/// on screen, each once while the folder is open, and none of those scrolled past before they
/// were read. Until it is read an item shows its type's icon. The pictures are in an image list,
/// one for each item shown, at the list's scale, which the list draws from.
/// </summary>
sealed partial class Thumbnails : IDisposable
{
    /// <summary>
    /// How big Explorer's Large icons are, in pixels at 100% scale.
    /// </summary>
    public const int UnscaledSize = 96;

    // A few read at once. Each waits on the disk, and on the shell's handler for the file's type,
    // for as long as they take: a video's or a document's longer than a picture's.
    const int mostReaders = 3;

    // The most the pictures take, about 64 MB: a folder of thousands scrolled all the way through
    // would otherwise hold one for every item in it. Past it they are all let go, and those on
    // screen read again.
    const int mostBytes = 64 << 20;

    // SIID_LINK, the arrow the shell draws over a shortcut's icon.
    const uint stockShortcutArrow = 29;

    ImageList images;
    Func<string, Bitmap?> read;
    SynchronizationContext context;
    Action<string> arrived;
    Action letGo;

    // An item's picture in the image list, as of when the file was last written: one written
    // since is read again.
    sealed class Place(int index, DateTime modified, int overlay)
    {
        public int Index { get; } = index;
        public DateTime Modified { get; set; } = modified;
        public int Overlay { get; } = overlay;
        public bool Read { get; set; }
    }

    // On the UI thread alone: each item's picture, by its path; each type's icon, which an item
    // shows until its picture is read; and the arrow drawn over a shortcut's picture.
    Dictionary<string, Place> places = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<(int Icon, int Overlay), Bitmap> placeholders = [];
    Bitmap? arrow;
    bool arrowMade;

    // Shared with the readers, under the gate: what is still to be read, first shown first, what
    // is being read, how many readers there are, and how many times everything has been let go,
    // so that what a reader brings back of what was let go meanwhile is dropped.
    Lock gate = new();
    List<string> pending = [];
    HashSet<string> reading = new(StringComparer.OrdinalIgnoreCase);
    int readers;
    int generation;
    bool disposed;

    /// <param name="size">How big each picture is, in pixels, square.</param>
    /// <param name="read">Reads an item's picture, by its path, no bigger than the size given, as
    /// <see cref="FromShell"/> does. Called off the UI thread, on a thread set up for the shell's
    /// COM objects.</param>
    /// <param name="context">The UI thread, where what is read lands.</param>
    /// <param name="arrived">Told, on the UI thread, of each item whose picture has been read, by
    /// its path, for it to be drawn again.</param>
    /// <param name="letGo">Told when every picture has been let go, there being too many, for
    /// the rows showing them to be made again.</param>
    public Thumbnails(int size, Func<string, int, Bitmap?> read, SynchronizationContext context, Action<string> arrived, Action letGo)
    {
        Size = size;
        this.read = path => read(path, size);
        this.context = context;
        this.arrived = arrived;
        this.letGo = letGo;
        images = new()
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new(size, size)
        };

        // Made now, so each picture is copied in as it is added, as ShellListView's icons are.
        Handle = images.Handle;
    }

    /// <summary>
    /// How big each picture is, in pixels, square.
    /// </summary>
    public int Size { get; }

    /// <summary>
    /// The image list the pictures are in, for the list to draw from.
    /// </summary>
    public IntPtr Handle { get; }

    /// <summary>
    /// The items whose pictures have been read, by their paths. For tests.
    /// </summary>
    internal IReadOnlyList<string> ReadItems =>
        places
            .Where(_ => _.Value.Read)
            .Select(_ => _.Key)
            .ToList();

    /// <summary>
    /// Whether any picture is still to be read, or being read. For tests.
    /// </summary>
    internal bool Busy
    {
        get
        {
            lock (gate)
            {
                return pending.Count > 0 ||
                       reading.Count > 0;
            }
        }
    }

    /// <summary>
    /// Where in the image list an item's picture is, for its row. Put there the first time as its
    /// type's icon, <paramref name="icon"/> from the system image list with
    /// <paramref name="overlay"/> over it, until its own is read once it shows.
    /// </summary>
    public int Slot(FileEntry entry, int icon, int overlay)
    {
        if (places.TryGetValue(entry.FullPath, out var place))
        {
            // Written since it was read: read again once it shows, as it was until then.
            if (place.Modified != entry.Modified)
            {
                place.Modified = entry.Modified;
                place.Read = false;
            }

            return place.Index;
        }

        if (places.Count >= mostBytes / (Size * Size * 4))
        {
            Drop();
            letGo();
        }

        var index = images.Images.Count;
        images.Images.Add(Placeholder(icon, overlay));
        places[entry.FullPath] = new(index, entry.Modified, overlay);
        return index;
    }

    Bitmap Placeholder(int icon, int overlay)
    {
        if (!placeholders.TryGetValue((icon, overlay), out var placeholder))
        {
            placeholder = ShellIcons.Render(icon, new(Size, Size), Point.Empty, overlay, Size);
            placeholders[(icon, overlay)] = placeholder;
        }

        return placeholder;
    }

    /// <summary>
    /// Reads the pictures of the items on screen that are yet to be, first shown first, in place
    /// of any still to be read of those that were: scrolled past, they are not read. One being
    /// read is left to finish, and kept. Nothing in a zip, which isn't on disk to be read: there
    /// each shows its type's icon.
    /// </summary>
    public void Want(IReadOnlyList<FileEntry> shown)
    {
        lock (gate)
        {
            pending.Clear();
            foreach (var entry in shown)
            {
                if (!entry.InArchive &&
                    places.TryGetValue(entry.FullPath, out var place) &&
                    !place.Read &&
                    !reading.Contains(entry.FullPath))
                {
                    pending.Add(entry.FullPath);
                }
            }

            while (readers < mostReaders &&
                   readers < pending.Count)
            {
                StartReader();
            }
        }
    }

    /// <summary>
    /// Reads nothing more of what was to be read: nothing of the list shows, or what does is of
    /// a folder left. What is being read is kept.
    /// </summary>
    public void Cancel()
    {
        lock (gate)
        {
            pending.Clear();
        }
    }

    /// <summary>
    /// Lets go of every picture, and of reading any: the list shows another folder, or none.
    /// </summary>
    public void Drop()
    {
        lock (gate)
        {
            generation++;
            pending.Clear();
            reading.Clear();
        }

        if (places.Count == 0)
        {
            return;
        }

        places.Clear();
        images.Images.Clear();
    }

    // Under the gate.
    void StartReader()
    {
        readers++;
        var thread = new Thread(ReadPending)
        {
            IsBackground = true,
            Name = "Lode thumbnails"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // Reads what is pending, one after another, until there is nothing left, then ends.
    void ReadPending()
    {
        while (true)
        {
            string path;
            int readFor;
            lock (gate)
            {
                if (disposed ||
                    pending.Count == 0)
                {
                    readers--;
                    return;
                }

                path = pending[0];
                pending.RemoveAt(0);
                reading.Add(path);
                readFor = generation;
            }

            var picture = ReadFitted(path);
            try
            {
                context.Post(_ => Arrive(path, readFor, picture), null);
            }
            catch (InvalidOperationException)
            {
                // Lode is exiting, and there is no longer a UI thread to tell.
                picture?.Dispose();
            }
        }
    }

    // The item's picture at the foot of a square as big as every other's, across the middle, as
    // Explorer lines them up over their names. Null where nothing could be read: the item goes
    // on showing its type's icon.
    Bitmap? ReadFitted(string path)
    {
        Bitmap? picture;
        try
        {
            picture = read(path);
        }
        catch (Exception)
        {
            // Whatever reading it threw, the shell or its handler for the file's type: one item
            // can't be read, which mustn't end Lode, as a thread of its own throwing would.
            return null;
        }

        if (picture == null)
        {
            return null;
        }

        using (picture)
        {
            var scale = Math.Min(1, Math.Min(Size / (double) picture.Width, Size / (double) picture.Height));
            var width = Math.Max(1, (int) Math.Round(picture.Width * scale));
            var height = Math.Max(1, (int) Math.Round(picture.Height * scale));
            var fitted = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(fitted);
            if (scale < 1)
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }

            graphics.DrawImage(picture, new Rectangle((Size - width) / 2, Size - height, width, height));
            return fitted;
        }
    }

    // On the UI thread: a picture read, put in the item's place, unless everything has been let
    // go since it was asked for.
    void Arrive(string path, int readFor, Bitmap? picture)
    {
        using (picture)
        {
            lock (gate)
            {
                if (disposed ||
                    readFor != generation)
                {
                    return;
                }

                reading.Remove(path);
            }

            if (!places.TryGetValue(path, out var place))
            {
                return;
            }

            place.Read = true;
            if (picture != null)
            {
                // What a shortcut points at, with the arrow over it, as Explorer draws it.
                if (place.Overlay != 0 &&
                    Arrow() is { } shortcut)
                {
                    using var graphics = Graphics.FromImage(picture);
                    graphics.DrawImage(shortcut, 0, 0, Size, Size);
                }

                images.Images[place.Index] = picture;
            }

            arrived(path);
        }
    }

    // The shortcut arrow, as big as the pictures, or null where the shell has none to give.
    Bitmap? Arrow()
    {
        if (arrowMade)
        {
            return arrow;
        }

        arrowMade = true;
        if (TaskbarIdentity.StockIcon(stockShortcutArrow) is { } stock)
        {
            arrow = ShellIcons.Render(ShellIcons.GetResourceIcon(stock.File, stock.Index), new(Size, Size), Point.Empty, 0, Size);
        }

        return arrow;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            generation++;
            pending.Clear();
        }

        images.Dispose();
        foreach (var placeholder in placeholders.Values)
        {
            placeholder.Dispose();
        }

        arrow?.Dispose();
    }

    // IID_IShellItemImageFactory, and SIIGBF_RESIZETOFIT: the thumbnail, or else the icon, no
    // bigger than asked, shaped as it is.
    static Guid imageFactoryInterface = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    const int resizeToFit = 0;

    /// <summary>
    /// What the shell shows of an item in a view of big icons: its thumbnail, where its type's
    /// handler can make one, or else its icon, either no bigger than <paramref name="size"/>
    /// pixels square, as IShellItemImageFactory gives it. Null where the shell has neither. To be
    /// called on a thread set up for the shell's COM objects.
    /// </summary>
    public static unsafe Bitmap? FromShell(string path, int size)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, imageFactoryInterface, out var factory) < 0)
        {
            return null;
        }

        try
        {
            // Through the factory's own pointer, its one method after IUnknown's three, as
            // DragImage calls the drag helper: nothing of it outlives the call.
            var methods = *(IntPtr**) factory;
            var getImage = (delegate* unmanaged[Stdcall]<IntPtr, PixelSize, int, IntPtr*, int>) methods[3];
            IntPtr bitmap;
            if (getImage(factory, new(size, size), resizeToFit, &bitmap) < 0 ||
                bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Copy(bitmap);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    // A copy of a bitmap the shell made, top row first, as GetDIBits gives it whichever way up it
    // is held. Its alpha is as it is, not premultiplied. Where there is none at all, every pixel's
    // 0, as a handler may give a picture with no transparency, it is opaque.
    static unsafe Bitmap? Copy(IntPtr handle)
    {
        BitmapDescription description;
        if (GetObject(handle, sizeof(BitmapDescription), &description) == 0 ||
            description.Width <= 0 ||
            description.Height <= 0)
        {
            return null;
        }

        var width = description.Width;
        var height = description.Height;
        // A negative height asks for the top row first.
        var header = new BitmapInfo
        {
            Header = new()
            {
                Size = sizeof(BitmapHeader),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            }
        };
        var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = copy.LockBits(new(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int lines;
        try
        {
            var screen = GetDC(IntPtr.Zero);
            try
            {
                lines = GetDIBits(screen, handle, 0, (uint) height, data.Scan0, ref header, 0);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            var pixels = new Span<byte>((void*) data.Scan0, data.Stride * height);
            if (!HasAlpha(pixels))
            {
                for (var alpha = 3; alpha < pixels.Length; alpha += 4)
                {
                    pixels[alpha] = 255;
                }
            }
        }
        finally
        {
            copy.UnlockBits(data);
        }

        if (lines != height)
        {
            copy.Dispose();
            return null;
        }

        return copy;
    }

    static bool HasAlpha(Span<byte> pixels)
    {
        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            if (pixels[alpha] != 0)
            {
                return true;
            }
        }

        return false;
    }

    // SIZE.
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct PixelSize(int Width, int Height);

    // BITMAP, as GetObject describes a bitmap.
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapDescription
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    // BITMAPINFOHEADER.
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

    // BITMAPINFO: the header, then room for the colour masks GetDIBits may write after it.
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public BitmapHeader Header;
        public int RedMask;
        public int GreenMask;
        public int BlueMask;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid interfaceId, out IntPtr item);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static unsafe partial int GetObject(IntPtr handle, int size, void* description);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr context, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo header, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr context);
}
