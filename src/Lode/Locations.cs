static class Locations
{
    public const string ThisPC = "This PC";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool IsThisPC(string location) =>
        string.Equals(location, ThisPC, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How the shell names This PC: to a verb's command, the explore verb on Folder say, and to
    /// the shell when Lode asks for This PC's own menu.
    /// </summary>
    public const string ThisPCParsingName = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// True for a virtual folder as the shell names it, "::{CLSID}" and what follows (Control
    /// Panel, the Recycle Bin, a library), other than This PC, which Lode shows.
    /// </summary>
    public static bool IsVirtual(string location) =>
        location.StartsWith("::", StringComparison.Ordinal) &&
        !location.Equals(ThisPCParsingName, StringComparison.OrdinalIgnoreCase);

    public static bool IsNetwork(CharSpan location) =>
        location.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// The machine a share is on, "\\server" for "\\server\share\folder", or null for somewhere
    /// not on the network.
    /// </summary>
    public static string? Server(string location)
    {
        if (!IsNetwork(location))
        {
            return null;
        }

        var end = location.IndexOf(Path.DirectorySeparatorChar, 2);
        if (end < 0)
        {
            return location;
        }

        return location[..end];
    }

    /// <summary>
    /// True for a machine on the network itself, "\\server", which lists the folders it shares.
    /// </summary>
    public static bool IsServer(string location) =>
        IsNetwork(location) &&
        location.Length > 2 &&
        location.IndexOf(Path.DirectorySeparatorChar, 2) < 0;

    /// <summary>
    /// True for a share itself, "\\server\share", rather than a folder in one.
    /// </summary>
    public static bool IsShare(string location) =>
        IsNetwork(location) &&
        !IsServer(location) &&
        Path.GetFileName(location.AsSpan()).IsEmpty;

    /// <summary>
    /// A share's own name, "share" for "\\server\share".
    /// </summary>
    public static string ShareName(string share)
    {
        var trimmed = share.AsSpan().TrimEnd(Path.DirectorySeparatorChar);
        return trimmed[(trimmed.LastIndexOf(Path.DirectorySeparatorChar) + 1)..].ToString();
    }

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

    /// <summary>
    /// Where <paramref name="path"/> is now that <paramref name="from"/>, it or a folder above
    /// it, has been moved or renamed to <paramref name="to"/>. Null for a path not within it.
    /// </summary>
    public static string? Moved(string path, string from, string to)
    {
        if (!IsWithin(path, from))
        {
            return null;
        }

        var rest = Path.TrimEndingDirectorySeparator(path.AsSpan())[Path.TrimEndingDirectorySeparator(from.AsSpan()).Length..];
        return string.Concat(Path.TrimEndingDirectorySeparator(to.AsSpan()), rest);
    }

    public static string? Normalize(CharSpan input)
    {
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"').ToString());
        if (text.Length == 0)
        {
            return null;
        }

        if (IsThisPC(text) ||
            text.Equals(ThisPCParsingName, StringComparison.OrdinalIgnoreCase))
        {
            return ThisPC;
        }

        if (text is [_, ':'])
        {
            text += Path.DirectorySeparatorChar;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            // "\\server\" is a root, which keeps its separator; the machine is "\\server".
            if (IsNetwork(full) &&
                full.Length > 3 &&
                full.IndexOf(Path.DirectorySeparatorChar, 2) == full.Length - 1)
            {
                return full[..^1];
            }

            return full;
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

        // A share is the root of its path, so .NET has no parent for it; it is on its machine.
        if (IsShare(location))
        {
            return Server(location);
        }

        return Path.GetDirectoryName(location) ?? ThisPC;
    }

    /// <summary>
    /// What is directly in <paramref name="ancestor"/> on the way down to
    /// <paramref name="location"/>: the drive, for This PC. Null when
    /// <paramref name="ancestor"/> is not above <paramref name="location"/>.
    /// </summary>
    public static string? ChildToward(string location, string ancestor)
    {
        var child = location;
        while (Parent(child) is { } parent)
        {
            if (Same(parent, ancestor))
            {
                return child;
            }

            child = parent;
        }

        return null;
    }

    public static string DisplayName(string location)
    {
        if (IsThisPC(location))
        {
            return ThisPC;
        }

        // A share is the root of its path, so it has no file name. It shows as Explorer names
        // it: its own name, then the machine it is on, "share (\\server)".
        if (IsShare(location))
        {
            return $"{ShareName(location)} ({Server(location)})";
        }

        if (IsServer(location))
        {
            return location[2..];
        }

        var name = Path.GetFileName(location);
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
    /// "D:\src". A folder in a share says which machine it is on, "tv (\\server)", as a share
    /// does, so it reads apart from a folder on this machine. Keyed by location, ignoring case.
    /// </summary>
    public static Dictionary<string, string> Labels(IEnumerable<string> locations)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sameNames = locations
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(_ => OnServer(_, DisplayName(_)), StringComparer.OrdinalIgnoreCase)
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
            return OnServer(location, DisplayName(location));
        }

        for (var depth = 1; ; depth++)
        {
            var label = Tail(location, depth);
            if (label.Length == location.Length)
            {
                return label;
            }

            if (sameName.All(_ => ReferenceEquals(_, location) ||
                                  !Tail(_, depth).Equals(label, StringComparison.OrdinalIgnoreCase)))
            {
                return OnServer(location, label);
            }
        }
    }

    // A folder in a share's label, with the machine it is on after it. A share's name has it
    // already, and a machine is its own name.
    static string OnServer(string location, string label)
    {
        if (!IsNetwork(location) ||
            IsServer(location) ||
            IsShare(location))
        {
            return label;
        }

        return $"{label} ({Server(location)})";
    }

    // The location's name with the names of depth folders above it, or the whole location once
    // that reaches into its root, which means nothing without the rest of it: "C:" alone. On
    // the network the root is the machine, which the label names after the tail, so a share's
    // name can be part of one.
    static string Tail(string location, int depth)
    {
        var root = Path.GetPathRoot(location.AsSpan()).Length;
        if (Server(location) is { } server)
        {
            root = server.Length + 1;
        }

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
