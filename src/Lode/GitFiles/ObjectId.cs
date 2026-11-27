/// <summary>
/// The SHA-1 git names an object by: twenty bytes, held most significant first so that ordering
/// the fields orders the ids as git sorts them.
/// </summary>
readonly struct ObjectId :
    IEquatable<ObjectId>
{
    public const int Length = 20;

    readonly ulong first;
    readonly ulong second;
    readonly uint third;

    public ObjectId(ByteSpan bytes)
    {
        first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        third = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
    }

    /// <summary>
    /// The first byte, which a pack index and a commit graph fan out by.
    /// </summary>
    public int FirstByte => (int)(first >> 56);

    public void CopyTo(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64BigEndian(bytes, first);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], second);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[16..], third);
    }

    /// <summary>
    /// Forty hex digits, as a loose ref or packed-refs has an id.
    /// </summary>
    public static bool TryParse(ByteSpan hex, out ObjectId id)
    {
        id = default;
        if (hex.Length < 2 * Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];
        for (var index = 0; index < Length; index++)
        {
            var high = HexValue(hex[2 * index]);
            var low = HexValue(hex[2 * index + 1]);
            if (high < 0 ||
                low < 0)
            {
                return false;
            }

            bytes[index] = (byte)(high << 4 | low);
        }

        id = new(bytes);
        return true;
    }

    public static bool TryParse(CharSpan hex, out ObjectId id)
    {
        id = default;
        if (hex.Length != 2 * Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];
        if (Convert.FromHexString(hex, bytes, out _, out var written) != OperationStatus.Done ||
            written != Length)
        {
            return false;
        }

        id = new(bytes);
        return true;
    }

    static int HexValue(byte digit)
    {
        if (digit is >= (byte)'0' and <= (byte)'9')
        {
            return digit - '0';
        }

        if (digit is >= (byte)'a' and <= (byte)'f')
        {
            return digit - 'a' + 10;
        }

        if (digit is >= (byte)'A' and <= (byte)'F')
        {
            return digit - 'A' + 10;
        }

        return -1;
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Length];
        CopyTo(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public bool Equals(ObjectId other) =>
        first == other.first &&
        second == other.second &&
        third == other.third;

    public override bool Equals(object? obj) =>
        obj is ObjectId other &&
        Equals(other);

    // An id is a hash already, so any part of it spreads as well as the whole.
    public override int GetHashCode() =>
        (int)first;

    /// <summary>
    /// How this id compares with the twenty bytes at the start of <paramref name="bytes"/>, as a
    /// pack index or commit graph holds them, without making an id of them.
    /// </summary>
    public int CompareTo(ByteSpan bytes)
    {
        var compared = first.CompareTo(BinaryPrimitives.ReadUInt64BigEndian(bytes));
        if (compared != 0)
        {
            return compared;
        }

        compared = second.CompareTo(BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
        if (compared != 0)
        {
            return compared;
        }

        return third.CompareTo(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]));
    }

    public static bool operator ==(ObjectId left, ObjectId right) =>
        left.Equals(right);

    public static bool operator !=(ObjectId left, ObjectId right) =>
        !left.Equals(right);
}

/// <summary>
/// Thrown where a repository holds what isn't read here as git would read it: a format newer
/// than is known, a filter only git can run, an object missing. Whoever reads the repository in
/// process then asks git instead.
/// </summary>
sealed class GitFilesException(string message) :
    Exception(message);
