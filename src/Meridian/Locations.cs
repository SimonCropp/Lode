static class Locations
{
    public const string ThisPC = "This PC";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool IsThisPC(string location) =>
        string.Equals(location, ThisPC, StringComparison.OrdinalIgnoreCase);

    public static bool IsNetwork(string location) =>
        location.StartsWith(@"\\", StringComparison.Ordinal);

    public static bool Same(string? first, string? second) =>
        first != null &&
        second != null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> or somewhere beneath it.
    /// </summary>
    public static bool IsWithin(string path, string folder)
    {
        if (Same(path, folder))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? Normalize(string input)
    {
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (text.Length == 0)
        {
            return null;
        }

        if (IsThisPC(text))
        {
            return ThisPC;
        }

        if (text.Length == 2 && text[1] == ':')
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
            name = location.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar)[^1];
        }

        return name.Length == 0 ? location : name;
    }

    public static bool SameRoot(string first, string second) =>
        string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase);
}
