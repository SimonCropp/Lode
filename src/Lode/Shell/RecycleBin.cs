/// <summary>
/// Putting deleted items back, by the paths Windows gave them in the Recycle Bin.
/// </summary>
static class RecycleBin
{
    // ShellSpecialFolderConstants.ssfBITBUCKET.
    const int recycleBin = 10;

    /// <summary>
    /// Restores the items to where they were deleted from, as the Recycle Bin's Restore does.
    /// Returns the paths of those not restored: no longer in the Recycle Bin, emptied since say,
    /// or still there, skipped at the shell's prompt for something of the same name where it
    /// goes back to.
    /// </summary>
    public static IReadOnlyCollection<string> Restore(IReadOnlyCollection<string> binPaths)
    {
        var wanted = new HashSet<string>(binPaths, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (!wanted.Contains(path))
                {
                    continue;
                }

                // Done once it has left the Recycle Bin, not when the verb returns.
                item.InvokeVerb("undelete");
                if (!Path.Exists(path))
                {
                    wanted.Remove(path);
                }

                found.Add(path);
                if (found.Count == binPaths.Count)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // What was not found is reported back.
        }

        return wanted;
    }
}
