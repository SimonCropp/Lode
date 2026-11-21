/// <summary>
/// What Show system files covers, hidden while it is off: what Windows marks both hidden and
/// system (Explorer's protected operating system files), and the folders that belong to Windows
/// or to other people: the Windows folder, Program Files, Program Files (x86), and other users'
/// folders under Users. The user's own folder and Public stay.
/// </summary>
sealed class SystemItems(string windows, IReadOnlyList<string> programFiles, string users, IReadOnlyList<string> keptUsers)
{
    public static SystemItems ForThisMachine { get; } = Create();

    static SystemItems Create()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var users = Path.GetDirectoryName(profile) ?? "";
        List<string> kept = [profile];
        if (Environment.GetEnvironmentVariable("PUBLIC") is { Length: > 0 } publicFolder)
        {
            kept.Add(publicFolder);
        }

        string[] programFiles =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];
        return new(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            programFiles.Where(_ => _.Length > 0).ToList(),
            users,
            kept);
    }

    const FileAttributes protectedFile = FileAttributes.Hidden | FileAttributes.System;

    public bool IsSystem(string fullPath, FileAttributes attributes)
    {
        if ((attributes & protectedFile) == protectedFile)
        {
            return true;
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            return false;
        }

        if (Locations.Same(fullPath, windows) ||
            programFiles.Any(_ => Locations.Same(fullPath, _)))
        {
            return true;
        }

        return users.Length > 0 &&
               Locations.Same(Path.GetDirectoryName(fullPath), users) &&
               !keptUsers.Any(_ => Locations.Same(fullPath, _));
    }
}
