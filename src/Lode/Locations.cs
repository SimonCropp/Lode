static class Locations
{
    public const string ThisPC = "This PC";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool IsThisPC(string location) =>
        string.Equals(location, ThisPC, StringComparison.OrdinalIgnoreCase);

    // How the shell names This PC to a verb's command, the explore verb on Folder say.
    const string thisPCParsingName = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// True for a virtual folder as the shell names it, "::{CLSID}" and what follows (Control
    /// Panel, the Recycle Bin, a library), other than This PC, which Lode shows.
    /// </summary>
    public static bool IsVirtual(string location) =>
        location.StartsWith("::", StringComparison.Ordinal) &&
        !location.Equals(thisPCParsingName, StringComparison.OrdinalIgnoreCase);

    public static bool IsNetwork(CharSpan location) =>
        location.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// True for somewhere on the network: a share, or a drive letter mapped to one. Asking
    /// whether such a place is there can take a long time to be answered when it is not, so it
    /// is not asked on the UI thread. Finding out which a drive is asks nothing of the network.
    /// </summary>
    public static bool IsRemote(string location)
    {
        if (IsNetwork(location))
        {
            return true;
        }

        if (Path.GetPathRoot(location) is not [_, ':', ..] root)
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool Same(string? first, string? second) =>
        first != null &&
        second != null &&
        Same(first.AsSpan(), second.AsSpan());

    public static bool Same(CharSpan first, CharSpan second) =>
        Path.TrimEndingDirectorySeparator(first)
            .Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="path"/> is directly inside <paramref name="folder"/>.
    /// </summary>
    public static bool IsChildOf(CharSpan path, CharSpan folder)
    {
        var parent = Path.GetDirectoryName(path);
        return !parent.IsEmpty &&
               Same(parent, folder);
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> or somewhere beneath it.
    /// </summary>
    public static bool IsWithin(CharSpan path, CharSpan folder)
    {
        if (Same(path, folder))
        {
            return true;
        }

        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.EndsInDirectorySeparator(folder) ||
               (path.Length > folder.Length && path[folder.Length] == Path.DirectorySeparatorChar);
    }

    public static string? Normalize(CharSpan input)
    {
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"').ToString());
        if (text.Length == 0)
        {
            return null;
        }

        if (IsThisPC(text) ||
            text.Equals(thisPCParsingName, StringComparison.OrdinalIgnoreCase))
        {
            return ThisPC;
        }

        if (text is [_, ':'])
        {
            text += Path.DirectorySeparatorChar;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? Parent(string location)
    {
        if (IsThisPC(location))
        {
            return null;
        }

        return Path.GetDirectoryName(location) ?? ThisPC;
    }

    public static string DisplayName(string location)
    {
        if (IsThisPC(location))
        {
            return ThisPC;
        }

        var name = Path.GetFileName(location);
        if (name.Length == 0 && IsNetwork(location))
        {
            // A share is the root of its path, so it has no file name; show the share's name.
            var share = location.AsSpan().TrimEnd(Path.DirectorySeparatorChar);
            name = share[(share.LastIndexOf(Path.DirectorySeparatorChar) + 1)..].ToString();
        }

        if (name.Length == 0)
        {
            return location;
        }

        return name;
    }

    /// <summary>
    /// What a tab or a window's title calls each of <paramref name="locations"/>: its name, or,
    /// where another has the same name, as much of the end of its path as tells the two apart,
    /// "Lode\src" and "Other\src", and the whole path when only the root does, "C:\src" and
    /// "D:\src". Keyed by location, ignoring case.
    /// </summary>
    public static Dictionary<string, string> Labels(IEnumerable<string> locations)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sameNames = locations
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(_ => _.ToList());
        foreach (var sameName in sameNames)
        {
            foreach (var location in sameName)
            {
                labels[location] = Label(location, sameName);
            }
        }

        return labels;
    }

    static string Label(string location, List<string> sameName)
    {
        if (sameName.Count == 1)
        {
            return DisplayName(location);
        }

        for (var depth = 1; ; depth++)
        {
            var label = Tail(location, depth);
            if (label.Length == location.Length ||
                sameName.All(_ => ReferenceEquals(_, location) ||
                                  !Tail(_, depth).Equals(label, StringComparison.OrdinalIgnoreCase)))
            {
                return label;
            }
        }
    }

    // The location's name with the names of depth folders above it, or the whole location once
    // that reaches into its root, which means nothing without the rest of it: a share's name
    // without its server's.
    static string Tail(string location, int depth)
    {
        var root = Path.GetPathRoot(location.AsSpan()).Length;
        var start = location.Length;
        for (var step = 0; step <= depth; step++)
        {
            start = location.AsSpan(0, start).LastIndexOf(Path.DirectorySeparatorChar);
            // C:\'s own separator can start a tail; a separator inside \\server\share cannot.
            if (start < 0 ||
                start < root - 1)
            {
                return location;
            }
        }

        return location[(start + 1)..];
    }

    public static bool SameRoot(CharSpan first, CharSpan second) =>
        Path.GetPathRoot(first)
            .Equals(Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase);
}
