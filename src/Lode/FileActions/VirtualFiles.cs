using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComFormat = System.Runtime.InteropServices.ComTypes.FORMATETC;
using ComMedium = System.Runtime.InteropServices.ComTypes.STGMEDIUM;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;
using ComStatistics = System.Runtime.InteropServices.ComTypes.STATSTG;
using ComTypes = System.Runtime.InteropServices.ComTypes;

/// <summary>
/// Files dragged that are not on disk: an attachment from Outlook, a photo from a phone through
/// Explorer, what is in a zip Explorer has open. They come in two formats: FileGroupDescriptorW,
/// naming each file and folder, a folder ahead of what is in it, with whichever of its size and
/// times the source knows, and FileContents, each file's bytes, asked for by its place in the
/// descriptor, as a stream, a block of memory or, for an Outlook message, a compound file.
/// Explorer takes them, as Lode does: it writes them into a folder of its own, then puts them
/// where they were dropped as it puts any files there.
/// </summary>
static partial class VirtualFiles
{
    public const string DescriptorFormat = "FileGroupDescriptorW";
    public const string ContentsFormat = "FileContents";

    /// <summary>
    /// What the descriptor says of one item: its name, which may hold the folders it is in,
    /// whether it is a folder, and whichever of its size and times it gives.
    /// <paramref name="Index"/> is its place in the descriptor, by which its bytes are asked for.
    /// </summary>
    public sealed record Descriptor(int Index, string Name, bool IsFolder, long? Size, DateTime? Created, DateTime? Accessed, DateTime? Written);

    /// <summary>
    /// An item written, by where it is under the folder it was written to.
    /// </summary>
    public sealed record Item(Descriptor Descriptor, string Relative);

    /// <summary>
    /// What was written into <paramref name="Folder"/>, Lode's own, ready to be put where it was
    /// dropped: <paramref name="Items"/>, in the descriptor's order. <paramref name="LeftOut"/>
    /// names those left out for their names, and <paramref name="Failed"/> those whose bytes
    /// couldn't be had, each with why.
    /// </summary>
    public sealed record Staged(string Folder, IReadOnlyList<Item> Items, IReadOnlyList<string> LeftOut, IReadOnlyList<string> Failed)
    {
        /// <summary>
        /// What is at the top of the folder, to be put where it was dropped.
        /// </summary>
        public List<string> TopLevel() =>
            Items
                .Select(_ => _.Relative.Split(Path.DirectorySeparatorChar)[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(_ => Path.Combine(Folder, _))
                .Where(Path.Exists)
                .ToList();

        /// <summary>
        /// Gives what was put where it was dropped, from <c>From</c> here to <c>To</c> there,
        /// the times the descriptor says, again: put on another drive, a file is copied, which
        /// gives it a new time of making. What is in a folder put there whole is seen to as well.
        /// </summary>
        public void Retime(IReadOnlyList<(string From, string To)> placed)
        {
            var byRelative = new Dictionary<string, Descriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var (descriptor, relative) in Items)
            {
                byRelative.TryAdd(relative, descriptor);
            }

            var folders = new List<(string Path, Descriptor Descriptor)>();
            foreach (var (from, to) in placed)
            {
                var relative = Path.GetRelativePath(Folder, from);
                if (Directory.Exists(to))
                {
                    foreach (var inside in Directory.EnumerateFileSystemEntries(to, "*", SearchOption.AllDirectories))
                    {
                        if (byRelative.TryGetValue(Path.Join(relative, Path.GetRelativePath(to, inside)), out var insideDescriptor))
                        {
                            Collect(inside, insideDescriptor);
                        }
                    }
                }

                if (byRelative.TryGetValue(relative, out var descriptor))
                {
                    Collect(to, descriptor);
                }
            }

            // A folder's after what is in it, the deepest first.
            foreach (var (path, descriptor) in folders.OrderByDescending(_ => _.Path.Length))
            {
                SetTimes(path, descriptor);
            }

            void Collect(string path, Descriptor descriptor)
            {
                if (descriptor.IsFolder)
                {
                    folders.Add((path, descriptor));
                    return;
                }

                SetTimes(path, descriptor);
            }
        }
    }

    /// <summary>
    /// Whether what is dragged is such files: it has both formats, and no paths, as files on
    /// disk have, which are taken as those.
    /// </summary>
    public static bool Offered(System.Windows.Forms.IDataObject? data) =>
        data != null &&
        !data.GetDataPresent(DataFormats.FileDrop) &&
        data.GetDataPresent(DescriptorFormat) &&
        data.GetDataPresent(ContentsFormat);

    /// <summary>
    /// Writes what is dragged into a new folder of Lode's own. Off the UI thread, on a thread of
    /// its own, where the source says it can be read once the drop has returned, as Explorer's
    /// and a phone's do, and is told when it is done with; otherwise before this returns, as
    /// Explorer reads one that can't, which may be gone once the drop is over. Null when it names
    /// nothing, or what it names can't be read.
    /// </summary>
    public static Task<Staged>? Stage(System.Windows.Forms.IDataObject data)
    {
        if (data is not ComDataObject com)
        {
            return null;
        }

        List<Descriptor> descriptors;
        try
        {
            descriptors = Describe(com);
        }
        catch (Exception exception)
            when (exception is
                COMException or
                InvalidCastException)
        {
            return null;
        }

        if (descriptors.Count == 0)
        {
            return null;
        }

        var (items, leftOut) = Plan(descriptors);
        var folder = Path.Combine(Path.GetTempPath(), "Lode", "Drops", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (Asynchronous(data) is { } source)
        {
            return StageAfterwards(source, items, leftOut, folder);
        }

        try
        {
            return Task.FromResult(new Staged(folder, items, leftOut, Write(com, items, folder)));
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    // Begins the source's operation, as it asks of a drop it lets go before it is read, and ends
    // it once read, on a thread of its own, saying whether it was copied.
    static Task<Staged> StageAfterwards(object source, List<Item> items, List<string> leftOut, string folder)
    {
        var capability = (IDataObjectAsyncCapability)source;
        capability.StartOperation(IntPtr.Zero);
        var dataObjectId = typeof(ComDataObject).GUID;
        var marshalled = CoMarshalInterThreadInterfaceInStream(ref dataObjectId, source, out var stream);
        if (marshalled < 0)
        {
            capability.EndOperation(marshalled, IntPtr.Zero, dropEffectNone);
            TryDelete(folder);
            Marshal.ThrowExceptionForHR(marshalled);
        }

        return FileOperations.OnStaThread(
            () =>
            {
                object? here = null;
                var result = failedResult;
                try
                {
                    Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, ref dataObjectId, out here));
                    var failed = Write((ComDataObject)here, items, folder);
                    var files = items.Count(_ => !_.Descriptor.IsFolder);
                    if (files == 0 ||
                        failed.Count < files)
                    {
                        result = 0;
                    }

                    return new Staged(folder, items, leftOut, failed);
                }
                catch
                {
                    TryDelete(folder);
                    throw;
                }
                finally
                {
                    End(here, result);
                }
            });
    }

    // Tells the source the drop is done with it, and whether it was copied, unless it has gone
    // meanwhile, its process closed say, which leaves nothing to tell.
    static void End(object? source, int result)
    {
        if (source == null)
        {
            return;
        }

        try
        {
            ((IDataObjectAsyncCapability)source).EndOperation(result, IntPtr.Zero, result == 0 ? dropEffectCopy : dropEffectNone);
        }
        catch (Exception exception)
            when (exception is
                COMException or
                InvalidCastException)
        {
            // Gone: nothing waits to hear.
        }
        finally
        {
            if (Marshal.IsComObject(source))
            {
                Marshal.ReleaseComObject(source);
            }
        }
    }

    const int failedResult = unchecked((int)0x80004005);
    const uint dropEffectNone = 0;
    const uint dropEffectCopy = 1;

    // The source that is to be read afterwards, when it says it can be.
    static object? Asynchronous(System.Windows.Forms.IDataObject data)
    {
        try
        {
            if (Source(data) is IDataObjectAsyncCapability capability)
            {
                capability.GetAsyncMode(out var asynchronous);
                if (asynchronous)
                {
                    return capability;
                }
            }
        }
        catch (Exception exception)
            when (exception is
                COMException or
                InvalidCastException)
        {
            // Read before the drop returns, as it is when it can't say.
        }

        return null;
    }

    /// <summary>
    /// The data object the drag's source gave, behind Windows Forms' own, to ask whether it can
    /// be read once the drop has returned. Windows Forms gives no way to it: one from another
    /// process it keeps in the Global Interface Table, by a cookie, and one of this process as
    /// it is, in fields of its own. Null where neither is found, in a Windows Forms that keeps
    /// them otherwise say, and the drop is then read before it returns, as for a source that
    /// can't be read after.
    /// </summary>
    internal static object? Source(System.Windows.Forms.IDataObject data)
    {
        if (data is not DataObject)
        {
            return data;
        }

        // What it was made from, a source of this process as it is, before a pointer it keeps:
        // made from one, it keeps a pointer of its own to that too.
        object? original = null;
        uint? cookie = null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            Find(data, 0);
        }
        catch (Exception exception)
            when (exception is
                MemberAccessException or
                TargetException or
                NotSupportedException)
        {
            return null;
        }

        if (original != null)
        {
            return original;
        }

        if (cookie is { } found)
        {
            return FromGlobalTable(found);
        }

        return null;

        void Find(object holder, int depth)
        {
            if (depth > 4 ||
                !seen.Add(holder))
            {
                return;
            }

            for (var type = holder.GetType(); type != null && IsWindowsForms(type); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsPointer ||
                        field.FieldType.IsByRefLike)
                    {
                        continue;
                    }

                    if (field.Name == "_cookie" &&
                        field.FieldType == typeof(uint) &&
                        type.Name.StartsWith("AgileComPointer", StringComparison.Ordinal))
                    {
                        cookie ??= (uint)field.GetValue(holder)!;
                        continue;
                    }

                    if (field.FieldType.IsPrimitive ||
                        field.GetValue(holder) is not { } value ||
                        value is string)
                    {
                        continue;
                    }

                    if (IsWindowsForms(value.GetType()))
                    {
                        Find(value, depth + 1);
                    }
                    else if (value is ComDataObject)
                    {
                        original ??= value;
                    }
                }
            }
        }
    }

    static bool IsWindowsForms(Type type) =>
        type.Assembly == typeof(DataObject).Assembly ||
        type.Assembly.GetName().Name == "System.Private.Windows.Core";

    static object? FromGlobalTable(uint cookie)
    {
        var table = (IGlobalInterfaceTable)new StandardGlobalInterfaceTable();
        try
        {
            var dataObjectId = typeof(ComDataObject).GUID;
            if (table.GetInterfaceFromGlobal(cookie, ref dataObjectId, out var pointer) < 0)
            {
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(table);
        }
    }

    /// <summary>
    /// The items <paramref name="data"/>'s descriptor names, in its order.
    /// </summary>
    public static List<Descriptor> Describe(ComDataObject data)
    {
        var format = Format(DescriptorFormat, -1, ComTypes.TYMED.TYMED_HGLOBAL);
        data.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != ComTypes.TYMED.TYMED_HGLOBAL ||
                medium.unionmember == IntPtr.Zero)
            {
                return [];
            }

            var pointer = GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                return Parse(pointer, (long)GlobalSize(medium.unionmember));
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

    // FILEGROUPDESCRIPTORW: a count, then a FILEDESCRIPTORW for each, of these sizes and places.
    const int descriptorSize = 592;
    const int attributesOffset = 36;
    const int createdOffset = 40;
    const int accessedOffset = 48;
    const int writtenOffset = 56;
    const int sizeHighOffset = 64;
    const int sizeLowOffset = 68;
    const int nameOffset = 72;
    const int nameLength = 260;

    // What each FILEDESCRIPTORW's flags say it gives.
    const uint givesAttributes = 0x4;
    const uint givesCreated = 0x8;
    const uint givesAccessed = 0x10;
    const uint givesWritten = 0x20;
    const uint givesSize = 0x40;
    const uint directoryAttribute = 0x10;

    static List<Descriptor> Parse(IntPtr pointer, long size)
    {
        var descriptors = new List<Descriptor>();
        if (size < sizeof(int))
        {
            return descriptors;
        }

        var count = Marshal.ReadInt32(pointer);
        for (var index = 0; index < count && sizeof(int) + (index + 1L) * descriptorSize <= size; index++)
        {
            var item = pointer + sizeof(int) + index * descriptorSize;
            var flags = (uint)Marshal.ReadInt32(item);
            var name = Marshal.PtrToStringUni(item + nameOffset, nameLength);
            var end = name.IndexOf('\0');
            if (end >= 0)
            {
                name = name[..end];
            }

            long? fileSize = null;
            if ((flags & givesSize) != 0)
            {
                fileSize = ((long)(uint)Marshal.ReadInt32(item, sizeHighOffset) << 32) | (uint)Marshal.ReadInt32(item, sizeLowOffset);
            }

            descriptors.Add(
                new(
                    index,
                    name,
                    (flags & givesAttributes) != 0 &&
                    ((uint)Marshal.ReadInt32(item, attributesOffset) & directoryAttribute) != 0,
                    fileSize,
                    Time(flags, givesCreated, item, createdOffset),
                    Time(flags, givesAccessed, item, accessedOffset),
                    Time(flags, givesWritten, item, writtenOffset)));
        }

        return descriptors;
    }

    static DateTime? Time(uint flags, uint gives, IntPtr item, int offset)
    {
        if ((flags & gives) == 0)
        {
            return null;
        }

        var fileTime = Marshal.ReadInt64(item, offset);
        if (fileTime <= 0 ||
            fileTime > DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return DateTime.FromFileTimeUtc(fileTime);
    }

    /// <summary>
    /// Where each item goes, under the folder it is written to, by its name, checked as a zip's
    /// entries are (see <see cref="Zip.Segments(string)"/>): one named to go outside it, with a
    /// name Windows can't have, or keeps for a device, is left out, and named in what is given
    /// back, as are those in a folder left out. A file named as one before it, but for case, as
    /// two attachments of an email often are, is numbered, "image001 (2).png", rather than
    /// written over it.
    /// </summary>
    public static (List<Item> Items, List<string> LeftOut) Plan(IReadOnlyList<Descriptor> descriptors)
    {
        var items = new List<Item>();
        var leftOut = new List<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            if (Zip.Segments(descriptor.Name) is not { Length: > 0 } segments ||
                InAFile(segments))
            {
                leftOut.Add(descriptor.Name);
                continue;
            }

            var relative = Path.Join(segments);
            if (descriptor.IsFolder)
            {
                if (files.Contains(relative))
                {
                    leftOut.Add(descriptor.Name);
                    continue;
                }

                AddFolders(segments, segments.Length);
                items.Add(new(descriptor, relative));
                continue;
            }

            AddFolders(segments, segments.Length - 1);
            relative = Numbered(relative);
            files.Add(relative);
            items.Add(new(descriptor, relative));
        }

        return (items, leftOut);

        // The folders an item is in, as many of its segments as depth: for a folder, all of
        // them, itself among them.
        void AddFolders(string[] segments, int depth)
        {
            for (var length = 1; length <= depth; length++)
            {
                folders.Add(Path.Join(segments[..length]));
            }
        }

        // Inside what is a file here already.
        bool InAFile(string[] segments)
        {
            for (var length = 1; length < segments.Length; length++)
            {
                if (files.Contains(Path.Join(segments[..length])))
                {
                    return true;
                }
            }

            return false;
        }

        string Numbered(string relative)
        {
            if (!files.Contains(relative) &&
                !folders.Contains(relative))
            {
                return relative;
            }

            var parent = Path.GetDirectoryName(relative) ?? "";
            var stem = Path.GetFileNameWithoutExtension(relative);
            var extension = Path.GetExtension(relative);
            for (var counter = 2;; counter++)
            {
                var numbered = Path.Join(parent, $"{stem} ({counter}){extension}");
                if (!files.Contains(numbered) &&
                    !folders.Contains(numbered))
                {
                    return numbered;
                }
            }
        }
    }

    /// <summary>
    /// Writes each item into <paramref name="folder"/>, which is there and empty, its bytes asked
    /// of <paramref name="data"/>, and gives each the times the descriptor says once all are
    /// written, so what is written in a folder doesn't change the folder's. Returns the names
    /// of those whose bytes couldn't be had, each with why, having written the rest.
    /// </summary>
    public static IReadOnlyList<string> Write(ComDataObject data, IReadOnlyList<Item> items, string folder)
    {
        var failed = new List<string>();
        var written = new List<(string Path, Descriptor Descriptor)>();
        foreach (var (descriptor, relative) in items)
        {
            var path = Path.Combine(folder, relative);
            if (descriptor.IsFolder)
            {
                Directory.CreateDirectory(path);
                written.Add((path, descriptor));
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteContents(data, descriptor, path);
                written.Add((path, descriptor));
            }
            catch (Exception exception)
                when (exception is
                    COMException or
                    IOException or
                    UnauthorizedAccessException or
                    InvalidCastException)
            {
                TryDelete(path);
                failed.Add($"{descriptor.Name}: {exception.Message}");
            }
        }

        // Folders after what is in them, the deepest first.
        foreach (var (path, descriptor) in written
                     .OrderBy(_ => _.Descriptor.IsFolder)
                     .ThenByDescending(_ => _.Path.Length))
        {
            SetTimes(path, descriptor);
        }

        return failed;
    }

    static void WriteContents(ComDataObject data, Descriptor descriptor, string path)
    {
        var format = Format(ContentsFormat, descriptor.Index, ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTORAGE);
        data.GetData(ref format, out var medium);
        try
        {
            switch (medium.tymed)
            {
                case ComTypes.TYMED.TYMED_ISTREAM:
                    WriteStream(medium.unionmember, path);
                    break;
                case ComTypes.TYMED.TYMED_HGLOBAL:
                    WriteMemory(medium.unionmember, descriptor.Size, path);
                    break;
                case ComTypes.TYMED.TYMED_ISTORAGE:
                    WriteStorage(medium.unionmember, path);
                    break;
                default:
                    throw new IOException($"It came as {medium.tymed}, which Lode can't read.");
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    static unsafe void WriteStream(IntPtr pointer, string path)
    {
        var stream = (ComStream)Marshal.GetObjectForIUnknown(pointer);
        try
        {
            // From its start: a source may give a stream it has read from already.
            try
            {
                stream.Seek(0, 0, IntPtr.Zero);
            }
            catch (Exception exception)
                when (exception is
                    COMException or
                    NotImplementedException)
            {
                // One that can't seek is read from where it is.
            }

            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1 << 20];
            while (true)
            {
                var read = 0;
                stream.Read(buffer, buffer.Length, (IntPtr)(&read));
                if (read <= 0)
                {
                    break;
                }

                file.Write(buffer, 0, read);
            }
        }
        finally
        {
            if (Marshal.IsComObject(stream))
            {
                Marshal.ReleaseComObject(stream);
            }
        }
    }

    // A block of memory may be bigger than what it holds, so no more of it than the descriptor
    // says the file is.
    static unsafe void WriteMemory(IntPtr memory, long? size, string path)
    {
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Its memory couldn't be read.");
        }

        try
        {
            var length = (long)GlobalSize(memory);
            if (size is { } known)
            {
                length = Math.Min(length, known);
            }

            using var source = new UnmanagedMemoryStream((byte*)pointer, length);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(file);
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    // A compound file, as Outlook gives a message dragged from it: a .msg.
    static void WriteStorage(IntPtr pointer, string path)
    {
        var source = (IStorage)Marshal.GetObjectForIUnknown(pointer);
        try
        {
            Marshal.ThrowExceptionForHR(StgCreateDocfile(path, storageCreate | storageReadWrite | storageShareExclusive, 0, out var target));
            try
            {
                source.CopyTo(0, IntPtr.Zero, IntPtr.Zero, target);
                target.Commit(0);
            }
            finally
            {
                Marshal.ReleaseComObject(target);
            }
        }
        finally
        {
            if (Marshal.IsComObject(source))
            {
                Marshal.ReleaseComObject(source);
            }
        }
    }

    const uint storageCreate = 0x1000;
    const uint storageReadWrite = 0x2;
    const uint storageShareExclusive = 0x10;

    static void SetTimes(string path, Descriptor descriptor)
    {
        try
        {
            if (descriptor.IsFolder)
            {
                SetTimes(path, descriptor, Directory.SetCreationTimeUtc, Directory.SetLastAccessTimeUtc, Directory.SetLastWriteTimeUtc);
                return;
            }

            SetTimes(path, descriptor, File.SetCreationTimeUtc, File.SetLastAccessTimeUtc, File.SetLastWriteTimeUtc);
        }
        catch (Exception exception)
            when (exception is
                IOException or
                UnauthorizedAccessException or
                ArgumentOutOfRangeException)
        {
            // Left with the times it was written at: what it holds is what matters.
        }
    }

    static void SetTimes(string path, Descriptor descriptor, Action<string, DateTime> created, Action<string, DateTime> accessed, Action<string, DateTime> written)
    {
        if (descriptor.Created is { } creation)
        {
            created(path, creation);
        }

        if (descriptor.Written is { } writing)
        {
            written(path, writing);
        }

        if (descriptor.Accessed is { } access)
        {
            accessed(path, access);
        }
    }

    /// <summary>
    /// Deletes Lode's own folder of what was dropped, with whatever is left in it.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
            when (exception is
                IOException or
                UnauthorizedAccessException)
        {
            // Left in the temp folder, which Windows clears.
        }
    }

    static ComFormat Format(string format, int index, ComTypes.TYMED tymed) =>
        new()
        {
            cfFormat = unchecked((short)DataFormats.GetFormat(format).Id),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = tymed
        };

    /// <summary>
    /// What a source tells a drop target that reads it once the drop has returned: Explorer's
    /// own data objects, and so a phone's, among them. IDataObjectAsyncCapability.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
    internal interface IDataObjectAsyncCapability
    {
        void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool asynchronous);

        void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool asynchronous);

        void StartOperation(IntPtr bindContext);

        void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inOperation);

        void EndOperation(int result, IntPtr bindContext, uint effects);
    }

    /// <summary>
    /// A compound file, as Outlook gives a message: IStorage.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000000B-0000-0000-C000-000000000046")]
    internal interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved, uint reservedToo, out ComStream stream);

        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved, uint mode, uint reservedToo, out ComStream stream);

        void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved, uint reservedToo, out IStorage storage);

        void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string? name, IStorage? priority, uint mode, IntPtr exclude, uint reserved, out IStorage storage);

        void CopyTo(uint excludeCount, IntPtr excludeIds, IntPtr excludeNames, IStorage destination);

        void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, uint flags);

        void Commit(uint flags);

        void Revert();

        void EnumElements(uint reserved, IntPtr reservedToo, uint reservedThree, out IntPtr enumerator);

        void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string name);

        void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string oldName, [MarshalAs(UnmanagedType.LPWStr)] string newName);

        void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr created, IntPtr accessed, IntPtr written);

        void SetClass(ref Guid classId);

        void SetStateBits(uint bits, uint mask);

        void Stat(out ComStatistics statistics, uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000146-0000-0000-C000-000000000046")]
    interface IGlobalInterfaceTable
    {
        [PreserveSig]
        int RegisterInterfaceInGlobal(IntPtr unknown, ref Guid interfaceId, out uint cookie);

        [PreserveSig]
        int RevokeInterfaceFromGlobal(uint cookie);

        [PreserveSig]
        int GetInterfaceFromGlobal(uint cookie, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport]
    [Guid("00000323-0000-0000-C000-000000000046")]
    class StandardGlobalInterfaceTable;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    internal static extern int StgCreateDocfile(string? name, uint mode, uint reserved, out IStorage storage);

    [DllImport("ole32.dll")]
    static extern int CoMarshalInterThreadInterfaceInStream(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] object unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object unknown);

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref ComMedium medium);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(IntPtr memory);
}
