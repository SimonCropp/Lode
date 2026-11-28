using System.Collections.Concurrent;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComFormat = System.Runtime.InteropServices.ComTypes.FORMATETC;
using ComMedium = System.Runtime.InteropServices.ComTypes.STGMEDIUM;
using ComTypes = System.Runtime.InteropServices.ComTypes;

/// <summary>
/// A drag's source of files that are not on disk, as Outlook's attachments and a phone's photos
/// are: it gives FileGroupDescriptorW, naming each, and FileContents, each one's bytes, in the
/// form it is made with. Says what it was asked for, and on which thread. Made asynchronous,
/// it says it can be read once the drop has returned, as Explorer's own data objects do, and
/// hears when the drop starts and ends its operation.
/// </summary>
sealed class VirtualSource(params VirtualSource.Entry[] entries) :
    ComDataObject,
    VirtualFiles.IDataObjectAsyncCapability
{
    public enum Form
    {
        Memory,
        Stream,
        Storage
    }

    /// <summary>
    /// One item: a folder where <paramref name="Bytes"/> is null. <paramref name="Size"/> is the
    /// size the descriptor says, which for a block of memory may be less than the block.
    /// </summary>
    public sealed record Entry(string Name, byte[]? Bytes = null, Form Form = Form.Memory, DateTime? Created = null, DateTime? Written = null, long? Size = null);

    public bool Asynchronous { get; init; }

    /// <summary>
    /// The threads FileContents was asked for on.
    /// </summary>
    public ConcurrentBag<int> ContentThreads { get; } = [];

    public List<string> Operations { get; } = [];

    public int? StartedOn { get; private set; }

    public TaskCompletionSource<(int Result, uint Effects)> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    static readonly short descriptorFormat = unchecked((short)DataFormats.GetFormat(VirtualFiles.DescriptorFormat).Id);
    static readonly short contentsFormat = unchecked((short)DataFormats.GetFormat(VirtualFiles.ContentsFormat).Id);

    // DV_E_FORMATETC, DV_E_LINDEX.
    const int noSuchFormat = unchecked((int)0x80040064);
    const int noSuchIndex = unchecked((int)0x80040068);

    public void GetData(ref ComFormat format, out ComMedium medium)
    {
        if (format.cfFormat == descriptorFormat)
        {
            medium = new()
            {
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
                unionmember = Descriptor()
            };
            return;
        }

        if (format.cfFormat != contentsFormat)
        {
            throw new COMException("Not offered", noSuchFormat);
        }

        ContentThreads.Add(Environment.CurrentManagedThreadId);
        if (format.lindex < 0 ||
            format.lindex >= entries.Length ||
            entries[format.lindex].Bytes is not { } bytes)
        {
            throw new COMException("No such file", noSuchIndex);
        }

        medium = entries[format.lindex].Form switch
        {
            Form.Stream => new()
            {
                tymed = ComTypes.TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(new BytesStream(bytes), typeof(ComTypes.IStream))
            },
            Form.Storage => new()
            {
                tymed = ComTypes.TYMED.TYMED_ISTORAGE,
                unionmember = Storage(bytes)
            },
            _ => new()
            {
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
                unionmember = Memory(bytes)
            }
        };
    }

    // A block bigger than what it holds, as GlobalAlloc's often are: what is past the size the
    // descriptor says is not the file's.
    static IntPtr Memory(byte[] bytes)
    {
        var memory = Marshal.AllocHGlobal(bytes.Length + 64);
        var padding = new byte[bytes.Length + 64];
        bytes.CopyTo(padding, 0);
        padding.AsSpan(bytes.Length).Fill(0xEE);
        Marshal.Copy(padding, 0, memory, padding.Length);
        return memory;
    }

    // A compound file, as Outlook gives a message, holding the bytes in a stream named Body,
    // deleted once released.
    static IntPtr Storage(byte[] bytes)
    {
        const uint create = 0x1000;
        const uint readWrite = 0x2;
        const uint shareExclusive = 0x10;
        const uint deleteOnRelease = 0x04000000;
        Marshal.ThrowExceptionForHR(VirtualFiles.StgCreateDocfile(null, create | readWrite | shareExclusive | deleteOnRelease, 0, out var storage));
        storage.CreateStream("Body", readWrite | shareExclusive, 0, 0, out var stream);
        stream.Write(bytes, bytes.Length, IntPtr.Zero);
        stream.Commit(0);
        Marshal.ReleaseComObject(stream);
        storage.Commit(0);
        var pointer = Marshal.GetComInterfaceForObject(storage, typeof(VirtualFiles.IStorage));
        Marshal.ReleaseComObject(storage);
        return pointer;
    }

    // FILEGROUPDESCRIPTORW: a count, then a FILEDESCRIPTORW of 592 bytes for each.
    IntPtr Descriptor()
    {
        const int size = 592;
        var bytes = new byte[4 + entries.Length * size];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var item = bytes.AsSpan(4 + index * size, size);

            // Attributes, the times given, size, and the progress UI flag Outlook sets.
            var flags = 0x4 | 0x4000u;
            BinaryPrimitives.WriteUInt32LittleEndian(item[36..], entry.Bytes == null ? 0x10u : 0x80u);
            if (entry.Created is { } created)
            {
                flags |= 0x8;
                BinaryPrimitives.WriteInt64LittleEndian(item[40..], created.ToFileTimeUtc());
            }

            if (entry.Written is { } written)
            {
                flags |= 0x20;
                BinaryPrimitives.WriteInt64LittleEndian(item[56..], written.ToFileTimeUtc());
            }

            if (entry.Bytes != null)
            {
                flags |= 0x40;
                var length = entry.Size ?? entry.Bytes.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(item[64..], (uint)(length >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(item[68..], (uint)length);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(item, flags);
            Encoding.Unicode.GetBytes(entry.Name).CopyTo(item[72..]);
        }

        var memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return memory;
    }

    public void GetDataHere(ref ComFormat format, ref ComMedium medium) =>
        throw new COMException("Not offered", noSuchFormat);

    public int QueryGetData(ref ComFormat format)
    {
        if (format.cfFormat == descriptorFormat ||
            format.cfFormat == contentsFormat)
        {
            return 0;
        }

        return noSuchFormat;
    }

    public int GetCanonicalFormatEtc(ref ComFormat formatIn, out ComFormat formatOut)
    {
        formatOut = formatIn;
        return unchecked((int)0x80040130);
    }

    public void SetData(ref ComFormat format, ref ComMedium medium, bool release)
    {
        // The drop's description and the like, which a source keeps or not.
        if (release)
        {
            ReleaseStgMedium(ref medium);
        }
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction) =>
        new Formats(
        [
            new()
            {
                cfFormat = descriptorFormat,
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ComTypes.TYMED.TYMED_HGLOBAL
            },
            new()
            {
                cfFormat = contentsFormat,
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_ISTORAGE
            }
        ]);

    public int DAdvise(ref ComFormat format, ComTypes.ADVF advf, ComTypes.IAdviseSink sink, out int connection)
    {
        connection = 0;
        return unchecked((int)0x80040003);
    }

    public void DUnadvise(int connection) =>
        throw new COMException("No connections", unchecked((int)0x80040003));

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA? enumerator)
    {
        enumerator = null;
        return unchecked((int)0x80040003);
    }

    public void SetAsyncMode(bool asynchronous)
    {
    }

    public void GetAsyncMode(out bool asynchronous) =>
        asynchronous = Asynchronous;

    public void StartOperation(IntPtr bindContext)
    {
        StartedOn = Environment.CurrentManagedThreadId;
        Operations.Add("start");
    }

    public void InOperation(out bool inOperation) =>
        inOperation = StartedOn != null && !Ended.Task.IsCompleted;

    public void EndOperation(int result, IntPtr bindContext, uint effects)
    {
        Operations.Add("end");
        Ended.TrySetResult((result, effects));
    }

    // A stream over bytes, as a source gives one from memory.
    sealed class BytesStream(byte[] bytes) : ComTypes.IStream
    {
        long position;

        public void Read(byte[] buffer, int count, IntPtr read)
        {
            var length = (int)Math.Min(count, bytes.Length - position);
            Array.Copy(bytes, position, buffer, 0, length);
            position += length;
            if (read != IntPtr.Zero)
            {
                Marshal.WriteInt32(read, length);
            }
        }

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            position = origin switch
            {
                1 => position + offset,
                2 => bytes.Length + offset,
                _ => offset
            };
            if (newPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(newPosition, position);
            }
        }

        public void Stat(out ComTypes.STATSTG statistics, int flags) =>
            statistics = new()
            {
                type = 2,
                cbSize = bytes.Length
            };

        public void Write(byte[] buffer, int count, IntPtr written) =>
            throw new NotSupportedException();

        public void SetSize(long size) =>
            throw new NotSupportedException();

        public void CopyTo(ComTypes.IStream destination, long count, IntPtr read, IntPtr written) =>
            throw new NotSupportedException();

        public void Commit(int flags)
        {
        }

        public void Revert() =>
            throw new NotSupportedException();

        public void LockRegion(long offset, long count, int lockType) =>
            throw new NotSupportedException();

        public void UnlockRegion(long offset, long count, int lockType) =>
            throw new NotSupportedException();

        public void Clone(out ComTypes.IStream clone) =>
            throw new NotSupportedException();
    }

    sealed class Formats(ComFormat[] formats) : ComTypes.IEnumFORMATETC
    {
        int next;

        public int Next(int count, ComFormat[] given, int[]? fetched)
        {
            var taken = 0;
            while (taken < count &&
                   next < formats.Length)
            {
                given[taken++] = formats[next++];
            }

            if (fetched is { Length: > 0 })
            {
                fetched[0] = taken;
            }

            return taken == count ? 0 : 1;
        }

        public int Skip(int count)
        {
            next = Math.Min(formats.Length, next + count);
            return next < formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            next = 0;
            return 0;
        }

        public void Clone(out ComTypes.IEnumFORMATETC clone) =>
            clone = new Formats(formats)
            {
                next = next
            };
    }

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref ComMedium medium);
}
