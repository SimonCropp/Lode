/// <summary>
/// A file's text, as Find in Files reads it: in the encoding its byte order mark says, UTF-8 or
/// UTF-16, or else UTF-8 when it is valid UTF-8, and Latin-1 when it isn't, which maps every
/// byte to a character and back, so a replace in such a file changes only the bytes it should.
/// A file with a nul in its first 8,000 bytes is binary, as git has it, and isn't read, unless a
/// UTF-16 mark says it is text. Read into buffers each worker keeps from one file to the next
/// (<see cref="Buffers"/>), as most files searched match nothing, and making a string of each,
/// or new buffers for it, would be most of the work.
/// </summary>
sealed class TextFile
{
    /// <summary>
    /// What a search's worker reads files into, kept from one to the next, so reading a file
    /// makes nothing new. Borrowing every buffer from the shared pool instead, many workers each
    /// reading files of every size, the pool kept making new ones, gigabytes of them. One over
    /// <see cref="mostKept"/>, a big file that may match, is borrowed from the pool, and given
    /// back by <see cref="Release"/> once the file is done with.
    /// </summary>
    public sealed class Buffers
    {
        const int mostKept = 4 * 1024 * 1024;
        byte[] bytes = new byte[64 * 1024];
        char[] chars = new char[64 * 1024];
        byte[]? lentBytes;
        char[]? lentChars;

        public byte[] Bytes(int length)
        {
            if (length > mostKept)
            {
                lentBytes ??= ArrayPool<byte>.Shared.Rent(length);
                return lentBytes;
            }

            return Grow(ref bytes, length);
        }

        public char[] Chars(int length)
        {
            if (length > mostKept)
            {
                lentChars ??= ArrayPool<char>.Shared.Rent(length);
                return lentChars;
            }

            return Grow(ref chars, length);
        }

        static T[] Grow<T>(ref T[] buffer, int length)
        {
            if (length > buffer.Length)
            {
                buffer = new T[BitOperations.RoundUpToPowerOf2((uint) length)];
            }

            return buffer;
        }

        /// <summary>
        /// Gives back what was borrowed for a big file, once what was read is done with.
        /// </summary>
        public void Release()
        {
            if (lentBytes != null)
            {
                ArrayPool<byte>.Shared.Return(lentBytes);
                lentBytes = null;
            }

            if (lentChars != null)
            {
                ArrayPool<char>.Shared.Return(lentChars);
                lentChars = null;
            }
        }
    }

    // How much of a big file is looked through at a time for whether it could match, before it
    // is read whole: most don't, and are then never read whole.
    const int chunk = 1024 * 1024;

    /// <summary>
    /// The biggest file searched. A log or a database bigger than this is very likely not what
    /// is looked for, and would take a while.
    /// </summary>
    public const long MostBytes = 64L * 1024 * 1024;

    const int binaryProbe = 8_000;

    // The byte order marks a file can start with. UTF-32's starts with UTF-16's.
    internal static ByteSpan Utf8Mark => [0xEF, 0xBB, 0xBF];
    internal static ByteSpan Utf16Mark => [0xFF, 0xFE];
    internal static ByteSpan Utf16BigEndianMark => [0xFE, 0xFF];
    internal static ByteSpan Utf32Mark => [0xFF, 0xFE, 0x00, 0x00];

    public enum Encoding
    {
        Utf8,
        Utf8WithMark,
        Utf16,
        Utf16BigEndian,
        Latin1
    }

    /// <summary>
    /// Why a file wasn't read.
    /// </summary>
    public enum Skip
    {
        None,
        Binary,
        TooLarge,
        Unreadable
    }

    readonly char[] buffer;
    readonly int length;

    public Encoding Kind { get; }

    TextFile(char[] buffer, int length, Encoding kind)
    {
        this.buffer = buffer;
        this.length = length;
        Kind = kind;
    }

    /// <summary>
    /// The text, in the buffer it was read into: as it is until the next file is read into it.
    /// </summary>
    public CharSpan Text => buffer.AsSpan(0, length);

    /// <summary>
    /// The file's text, or null and why not. Given what is looked for, its bytes are looked
    /// through first, and the file not decoded when they can't hold it: null, skipping nothing.
    /// </summary>
    public static TextFile? Read(string path, Buffers buffers, out Skip skip, Matcher? lookingFor = null)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            var size = RandomAccess.GetLength(handle);
            if (size > MostBytes)
            {
                skip = Skip.TooLarge;
                return null;
            }

            var count = (int) size;

            // Its start first, which says whether it is binary, as most of the bytes in a code
            // folder are: those files are then not read any further.
            Span<byte> start = stackalloc byte[binaryProbe];
            var probed = ReadAt(handle, start[..Math.Min(count, binaryProbe)], 0);
            if (IsBinary(start[..probed]))
            {
                skip = Skip.Binary;
                return null;
            }

            if (lookingFor is { CanRuleOut: true } &&
                count > chunk &&
                !MayBeIn(handle, count, lookingFor, buffers))
            {
                skip = Skip.None;
                return null;
            }

            var bytes = buffers.Bytes(Math.Max(count, 1));
            start[..probed].CopyTo(bytes);
            var read = probed;
            if (probed == Math.Min(count, binaryProbe))
            {
                read += ReadAt(handle, bytes.AsSpan(probed, count - probed), probed);
            }

            if (lookingFor != null &&
                !lookingFor.MayBeIn(bytes.AsSpan(0, read)))
            {
                skip = Skip.None;
                return null;
            }

            return Decode(bytes.AsSpan(0, read), out skip, buffers);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            skip = Skip.Unreadable;
            return null;
        }
    }

    // Whether a big file could hold what is looked for, looked through a chunk at a time in the
    // worker's own buffer, each chunk starting a little before the last ended, so a match across
    // the two is still seen.
    static bool MayBeIn(SafeFileHandle handle, int count, Matcher lookingFor, Buffers buffers)
    {
        var overlap = lookingFor.Overlap;
        var buffer = buffers.Bytes(chunk + overlap);
        for (long offset = 0; offset < count; offset += chunk)
        {
            var from = Math.Max(0, offset - overlap);
            var length = (int) Math.Min(chunk + (offset - from), count - from);
            var read = ReadAt(handle, buffer.AsSpan(0, length), from);
            if (lookingFor.MayBeIn(buffer.AsSpan(0, read), atStart: from == 0))
            {
                return true;
            }
        }

        return false;
    }

    // As much of the file as fills the buffer, from where it is told, or as there is.
    static int ReadAt(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = RandomAccess.Read(handle, buffer[read..], offset + read);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return read;
    }

    // Whether a file starting so is binary: a nul in it, without a UTF-16 mark to say the nuls
    // are half of each character.
    static bool IsBinary(ByteSpan start)
    {
        if (start.StartsWith(Utf32Mark))
        {
            return true;
        }

        if (start.StartsWith(Utf16Mark) ||
            start.StartsWith(Utf16BigEndianMark))
        {
            return false;
        }

        return start.Contains((byte) 0);
    }

    /// <summary>
    /// The text in <paramref name="bytes"/>, or null when they are binary. Into
    /// <paramref name="buffers"/> when given, otherwise a buffer of its own.
    /// </summary>
    internal static TextFile? Decode(ByteSpan bytes, out Skip skip, Buffers? buffers = null)
    {
        var kind = KindOf(bytes);
        if (kind == null)
        {
            skip = Skip.Binary;
            return null;
        }

        skip = Skip.None;
        var encoding = EncodingOf(kind.Value);
        var body = bytes[PreambleLength(kind.Value)..];
        var most = Math.Max(encoding.GetMaxCharCount(body.Length), 1);
        var chars = buffers?.Chars(most) ?? new char[most];
        var length = encoding.GetChars(body, chars);
        return new(chars, length, kind.Value);
    }

    // The encoding the bytes are in, or null for binary.
    static Encoding? KindOf(ByteSpan bytes)
    {
        if (bytes.StartsWith(Utf8Mark))
        {
            return Encoding.Utf8WithMark;
        }

        // UTF-32 is as good as binary here.
        if (bytes.StartsWith(Utf16Mark) &&
            !bytes.StartsWith(Utf32Mark))
        {
            return Encoding.Utf16;
        }

        if (bytes.StartsWith(Utf16BigEndianMark))
        {
            return Encoding.Utf16BigEndian;
        }

        if (bytes[..Math.Min(bytes.Length, binaryProbe)].Contains((byte) 0))
        {
            return null;
        }

        if (Utf8.IsValid(bytes))
        {
            return Encoding.Utf8;
        }

        return Encoding.Latin1;
    }

    static int PreambleLength(Encoding kind) =>
        kind switch
        {
            Encoding.Utf8WithMark => 3,
            Encoding.Utf16 or Encoding.Utf16BigEndian => 2,
            _ => 0
        };

    static UTF8Encoding utf8 = new(false);
    static UTF8Encoding utf8WithMark = new(true);
    static UnicodeEncoding utf16 = new(false, true);
    static UnicodeEncoding utf16BigEndian = new(true, true);

    static System.Text.Encoding EncodingOf(Encoding kind) =>
        kind switch
        {
            Encoding.Utf8 => utf8,
            Encoding.Utf8WithMark => utf8WithMark,
            Encoding.Utf16 => utf16,
            Encoding.Utf16BigEndian => utf16BigEndian,
            _ => System.Text.Encoding.Latin1
        };

    /// <summary>
    /// <paramref name="text"/> as bytes in <paramref name="kind"/>, its byte order mark first
    /// when it has one: what a file read as that is written back as.
    /// </summary>
    public static byte[] Encode(string text, Encoding kind)
    {
        var encoding = EncodingOf(kind);
        var preamble = encoding.GetPreamble();
        if (kind is Encoding.Utf8 or Encoding.Latin1)
        {
            preamble = [];
        }

        var bytes = new byte[preamble.Length + encoding.GetByteCount(text)];
        preamble.CopyTo(bytes, 0);
        encoding.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }
}
