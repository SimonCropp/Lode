record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    FileAttributes Attributes,
    bool IsDrive = false,
    long FreeSpace = 0,
    bool IsShare = false)
{
    /// <summary>
    /// A drive, or a share listed on its machine: somewhere to go, which can't be renamed,
    /// cut or deleted.
    /// </summary>
    public bool IsPlace => IsDrive || IsShare;

    public bool IsHidden => !IsDrive &&
                            Attributes.HasFlag(FileAttributes.Hidden);
}
