/// <param name="InArchive">In a zip, which Lode shows but doesn't change: opened, copied or
/// dragged out by extracting it first, never renamed, cut or deleted.</param>
/// <param name="Promoted">A file a folder down from a repository's root, listed at the root too
/// as a shortcut to it, because Options names it. See PromotedFiles. Opened, copied and dragged
/// as the file it is, but never renamed, cut or deleted from the root.</param>
record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    FileAttributes Attributes,
    bool IsDrive = false,
    long FreeSpace = 0,
    bool IsShare = false,
    bool InArchive = false,
    bool Promoted = false)
{
    // An entry made from another with a new path, by with, is in that path's folder.
    public string FullPath
    {
        get;
        init
        {
            field = value;
            Folder = FolderOf(value);
        }
    } = FullPath;

    /// <summary>
    /// The folder it is in, empty for a drive or a share, which sorting by Folder compares, over
    /// and over for what a big search found. Kept, rather than made from the path for every
    /// comparison, and one string for the entries in a folder, as they are read one after
    /// another.
    /// </summary>
    public string Folder { get; private init; } = FolderOf(FullPath);

    /// <summary>
    /// A drive, or a share listed on its machine: somewhere to go, which can't be renamed,
    /// cut or deleted.
    /// </summary>
    public bool IsPlace => IsDrive || IsShare;

    public bool IsHidden => !IsDrive &&
                            Attributes.HasFlag(FileAttributes.Hidden);

    // The folder last made on this thread, which the next entry made is most likely in too.
    [ThreadStatic]
    static string? lastFolder;

    static string FolderOf(string path)
    {
        var last = lastFolder;
        if (last != null &&
            Path.GetDirectoryName(path.AsSpan()).SequenceEqual(last))
        {
            return last;
        }

        return lastFolder = Path.GetDirectoryName(path) ?? "";
    }
}
