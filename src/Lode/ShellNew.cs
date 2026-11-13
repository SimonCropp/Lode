/// <summary>
/// The file types Windows offers under New on a folder's right-click menu, read from the
/// registry's ShellNew keys. Based on Files' ShellNewMenuHelper (Files Community, MIT), reading
/// only the places Explorer reads.
/// </summary>
static class ShellNew
{
    /// <summary>
    /// A type to create. A new file is a copy of <see cref="Template"/> when there is one,
    /// otherwise <see cref="Data"/>, otherwise empty.
    /// </summary>
    public record Entry(string Extension, string TypeName, byte[]? Data = null, string? Template = null);

    // Made by commands or wizards of their own, which Lode does not run.
    static HashSet<string> skipped =
    [
        with(StringComparer.OrdinalIgnoreCase),
        ".lnk",
        ".url",
        ".library-ms"
    ];

    static Lazy<IReadOnlyList<Entry>> current = new(() => Read(Registry.ClassesRoot, _ => ShellIcons.GetTypeName($"x{_}", false)));

    /// <summary>
    /// This machine's types, read once. Reading touches every file extension in the registry,
    /// so <see cref="Warm"/> does it early, off the UI thread.
    /// </summary>
    public static IReadOnlyList<Entry> Current => current.Value;

    public static void Warm() =>
        Task.Run(() => current.Value);

    /// <summary>
    /// The types under <paramref name="classes"/>, a stand-in for HKEY_CLASSES_ROOT, by type name.
    /// Text Document is always there, as Explorer always offers it.
    /// </summary>
    public static IReadOnlyList<Entry> Read(RegistryKey classes, Func<string, string> typeName)
    {
        var entries = new List<Entry>();
        foreach (var name in classes.GetSubKeyNames())
        {
            if (!name.StartsWith('.') ||
                skipped.Contains(name))
            {
                continue;
            }

            var entry = ReadExtension(classes, name, typeName);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        if (!entries.Any(_ => _.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new(".txt", typeName(".txt")));
        }

        return entries
            .OrderBy(_ => _.TypeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ShellNew sits on the extension, or under the extension's ProgID subkey.
    static Entry? ReadExtension(RegistryKey classes, string extension, Func<string, string> typeName)
    {
        try
        {
            using var key = classes.OpenSubKey(extension);
            if (key == null)
            {
                return null;
            }

            using var shellNew = key.OpenSubKey("ShellNew") ?? OpenUnderProgId(key);
            if (shellNew == null)
            {
                return null;
            }

            return ReadShellNew(shellNew, extension, typeName);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    static RegistryKey? OpenUnderProgId(RegistryKey extension)
    {
        if (extension.GetValue("") is not string progId ||
            progId.Length == 0)
        {
            return null;
        }

        return extension.OpenSubKey($@"{progId}\ShellNew");
    }

    static Entry? ReadShellNew(RegistryKey shellNew, string extension, Func<string, string> typeName)
    {
        var data = shellNew.GetValue("Data") switch
        {
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            _ => null
        };
        var template = shellNew.GetValue("FileName") as string;
        if (template?.Length == 0)
        {
            template = null;
        }

        var nullFile = shellNew.GetValueNames().Contains("NullFile", StringComparer.OrdinalIgnoreCase);
        if (data == null &&
            template == null &&
            !nullFile)
        {
            return null;
        }

        return new(extension, typeName(extension), data, template);
    }

    /// <summary>
    /// The file a ShellNew FileName value names: a full path as it is, otherwise looked up in
    /// the folders Explorer looks in. Null when it is in none of them.
    /// </summary>
    public static string? ResolveTemplate(string fileName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(fileName);
        if (Path.IsPathFullyQualified(expanded))
        {
            if (File.Exists(expanded))
            {
                return expanded;
            }

            return null;
        }

        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew")
        ];
        foreach (var folder in folders)
        {
            if (folder.Length == 0)
            {
                continue;
            }

            var path = Path.Combine(folder, expanded);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
