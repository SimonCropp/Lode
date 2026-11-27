using System.IO.MemoryMappedFiles;

/// <summary>
/// A file read in place through a map of it, as git reads its pack indexes, packs and commit
/// graphs: only what is looked at is read. Shared for writing and deleting, so git, repacking
/// meanwhile, isn't stopped; and to be let go of soon, as Windows won't delete a file that is
/// mapped.
/// </summary>
sealed unsafe class MappedFile : IDisposable
{
    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    readonly byte* pointer;

    public long Length { get; }

    MappedFile(MemoryMappedFile map, MemoryMappedViewAccessor view, long length)
    {
        this.map = map;
        this.view = view;
        Length = length;
        byte* start = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref start);
        pointer = start + view.PointerOffset;
    }

    /// <summary>
    /// The file mapped, or null where there is no such file, or it is empty.
    /// </summary>
    public static MappedFile? Open(string path)
    {
        FileStream stream;
        try
        {
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        }
        catch (Exception exception)
            when (
                exception is
                    FileNotFoundException or
                    DirectoryNotFoundException)
        {
            return null;
        }

        var length = stream.Length;
        if (length == 0)
        {
            stream.Dispose();
            return null;
        }

        // The map owns the stream from here, and lets go of it with itself.
        var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        return new(map, map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read), length);
    }

    /// <summary>
    /// The bytes from <paramref name="offset"/> on, as many as there are up to
    /// <paramref name="length"/>.
    /// </summary>
    public ByteSpan Span(long offset, int length)
    {
        if (offset < 0 ||
            offset > Length)
        {
            throw new GitFilesException("Read past the end of a file");
        }

        var available = Math.Min(length, Length - offset);
        return new(pointer + offset, (int)available);
    }

    /// <summary>
    /// The bytes from <paramref name="offset"/> to the end, up to as many as a span can hold.
    /// </summary>
    public ByteSpan From(long offset) =>
        Span(offset, int.MaxValue);

    public void Dispose()
    {
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        view.Dispose();
        map.Dispose();
    }
}
