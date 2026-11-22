/// <summary>
/// The files a folder down from a repository's root that Options names, listed at the root too,
/// as shortcuts to them: src\Lode.slnx say, opened from the root without going into src first.
/// Each pattern is a whole name, or a name with one *: what is before it is how the name starts,
/// what is after it how the name ends. Case is ignored.
/// </summary>
sealed class PromotedFiles
{
    readonly List<(string Prefix, string Suffix, bool Whole)> patterns;

    PromotedFiles(List<(string Prefix, string Suffix, bool Whole)> patterns) =>
        this.patterns = patterns;

    /// <summary>
    /// The patterns, one a line as Options has them. Blank lines are passed over, as is a pattern
    /// with more than one *, which is more than how a name starts and ends can say.
    /// </summary>
    public static PromotedFiles Parse(IEnumerable<string> patterns)
    {
        var parsed = new List<(string Prefix, string Suffix, bool Whole)>();
        foreach (var pattern in patterns)
        {
            var trimmed = pattern.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var star = trimmed.IndexOf('*');
            if (star < 0)
            {
                parsed.Add((trimmed, "", true));
                continue;
            }

            if (trimmed.IndexOf('*', star + 1) >= 0)
            {
                continue;
            }

            parsed.Add((trimmed[..star], trimmed[(star + 1)..], false));
        }

        return new(parsed);
    }

    public bool IsEmpty => patterns.Count == 0;

    /// <summary>
    /// Whether a pattern names the file. Both ends of a name with a * are its own, so
    /// Directory.*.props is not Directory.props. A loop, as it is asked of every file in every
    /// folder below a root.
    /// </summary>
    public bool Matches(CharSpan name)
    {
        foreach (var (prefix, suffix, whole) in patterns)
        {
            if (whole)
            {
                if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (name.Length >= prefix.Length + suffix.Length &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a change heard beneath a root is to a file promoted there: one in a folder of the
    /// root's own, that a pattern names. Asked of everything that changes beneath the root, the
    /// build's output included, so it makes no strings.
    /// </summary>
    public bool Promotes(string root, string path)
    {
        var folder = Path.GetDirectoryName(path.AsSpan());
        var above = Path.GetDirectoryName(folder);
        return Path.TrimEndingDirectorySeparator(above).Equals(Path.TrimEndingDirectorySeparator(root.AsSpan()), StringComparison.OrdinalIgnoreCase) &&
               Matches(Path.GetFileName(path.AsSpan()));
    }

    /// <summary>
    /// The files the patterns name in the folders listed at a root, which are what the root's own
    /// listing leaves in: hidden ones only when they are shown, and not what Show system files
    /// leaves out. Not .git, nor what other version control keeps for itself. A folder that can't
    /// be read adds nothing.
    /// </summary>
    public List<FileEntry> Find(List<FileEntry> listed, bool showHidden, SystemItems? hiddenSystem)
    {
        var found = new List<FileEntry>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? default : FileAttributes.Hidden
        };
        foreach (var folder in listed)
        {
            if (!folder.IsDirectory ||
                folder.IsPlace ||
                SearchWalk.IsVersionControl(folder.Name))
            {
                continue;
            }

            try
            {
                var files = new FileSystemEnumerable<FileEntry>(folder.FullPath, ToEntry, options)
                {
                    ShouldIncludePredicate = (ref entry) =>
                        !entry.IsDirectory &&
                        Matches(entry.FileName) &&
                        !SearchWalk.IsSystem(ref entry, hiddenSystem)
                };
                found.AddRange(files);
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException)
            {
                // Gone since the root was read, or not to be read: nothing to promote from it.
            }
        }

        return found;
    }

    static FileEntry ToEntry(ref FileSystemEntry entry) =>
        new(
            entry.FileName.ToString(),
            entry.ToFullPath(),
            false,
            entry.Length,
            entry.LastWriteTimeUtc.LocalDateTime,
            entry.Attributes,
            Promoted: true);
}
