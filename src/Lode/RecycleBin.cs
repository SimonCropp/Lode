/// <summary>
/// Putting deleted items back, by the paths Windows gave them in the Recycle Bin.
/// </summary>
static class RecycleBin
{
    // ShellSpecialFolderConstants.ssfBITBUCKET.
    const int recycleBin = 10;

    /// <summary>
    /// Restores the items to where they were deleted from, as the Recycle Bin's Restore does.
    /// Returns the paths of those no longer in the Recycle Bin, emptied since, say.
    /// </summary>
    public static List<string> Restore(IReadOnlyCollection<string> binPaths)
    {
        var wanted = new HashSet<string>(binPaths, StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
            {
                return [.. wanted];
            }

            dynamic shell = Activator.CreateInstance(type)!;
            foreach (var item in shell.Namespace(recycleBin).Items())
            {
                string path = item.Path;
                if (wanted.Remove(path))
                {
                    item.InvokeVerb("undelete");
                }
            }
        }
        catch (Exception)
        {
            // What was not found is reported back.
        }

        return [.. wanted];
    }
}
