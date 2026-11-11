record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    FileAttributes Attributes,
    bool IsDrive = false,
    long FreeSpace = 0)
{
    public bool IsHidden => !IsDrive &&
                            Attributes.HasFlag(FileAttributes.Hidden);
}
