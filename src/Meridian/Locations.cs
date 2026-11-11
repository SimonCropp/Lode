static class Locations
{
    public const string ThisPC = "This PC";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool IsThisPC(string location) =>
        string.Equals(location, ThisPC, StringComparison.OrdinalIgnoreCase);

    public static bool IsNetwork(CharSpan location) =>
        location.StartsWith(@"\\", StringComparison.Ordinal);

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

        if (IsThisPC(text))
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

    public static bool SameRoot(CharSpan first, CharSpan second) =>
        Path.GetPathRoot(first)
            .Equals(Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase);
}
