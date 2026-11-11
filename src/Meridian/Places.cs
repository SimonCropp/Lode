/// <summary>
/// The parts of the window that describe the machine rather than the folder being browsed:
/// the folders pinned to the top of the tree, whether drives are listed, and how free space
/// is reported. Passed in so tests can show a fixed set.
/// </summary>
sealed record Places(IReadOnlyList<string> Pinned, bool ShowComputer, Func<string, string> FreeSpace)
{
    // The shell folder behind Explorer's Quick access list.
    const string quickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public static Places ForThisMachine() =>
        new(QuickAccessFolders(), ShowComputer: true, DescribeFreeSpace);

    /// <summary>
    /// The folders in Explorer's Quick access list, in Explorer's order: the ones the user
    /// pinned, then the frequent ones Explorer adds itself. Pinning or unpinning a folder in
    /// Explorer is how this list is edited.
    /// </summary>
    static List<string> QuickAccessFolders()
    {
        var folders = new List<string>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
            {
                return folders;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            foreach (var item in shell.Namespace(quickAccess).Items())
            {
                // Quick access also lists recent files, and can hold virtual folders such as
                // the Recycle Bin, which have no path to browse.
                if (!(bool)item.IsFolder || !(bool)item.IsFileSystem)
                {
                    continue;
                }

                string path = item.Path;

                // An offline share takes a long time to say it isn't there, so network
                // folders are taken on trust.
                if (Locations.IsNetwork(path) || Directory.Exists(path))
                {
                    folders.Add(path);
                }
            }
        }
        catch (Exception)
        {
            // Without Quick access the tree still has This PC.
        }

        return folders;
    }

    static string DescribeFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is { Length: > 0 } && !Locations.IsNetwork(root))
            {
                var drive = new DriveInfo(root);
                return $"{Formatting.Size(drive.AvailableFreeSpace)} free of {Formatting.Size(drive.TotalSize)}";
            }
        }
        catch (Exception)
        {
            // Free space is informational only.
        }

        return "";
    }
}
