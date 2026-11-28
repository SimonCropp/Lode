/// <summary>
/// Finds installed text editors, and opens files in the one chosen in Options. Editors are
/// found the way GitHub Desktop finds them: each has known uninstall keys in the registry,
/// whose display name and publisher must match before the executable they point at is used.
/// </summary>
static class TextEditors
{
    public sealed record Editor(string Name, string Path);

    // Where an editor's executable is, from its uninstall key: a file in the InstallLocation
    // folder, or the DisplayIcon value itself.
    sealed record Definition(
        string Name,
        (RegistryHive Hive, string SubKey)[] Keys,
        string[] DisplayNamePrefixes,
        string[] Publishers,
        string? Executable);

    const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string wow64Uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    static (RegistryHive, string) User(string key) => (RegistryHive.CurrentUser, $@"{uninstall}\{key}");
    static (RegistryHive, string) Machine(string key) => (RegistryHive.LocalMachine, $@"{uninstall}\{key}");
    static (RegistryHive, string) Machine32(string key) => (RegistryHive.LocalMachine, $@"{wow64Uninstall}\{key}");

    // Keys, names and publishers from GitHub Desktop's app/src/lib/editors/win32.ts.
    static Definition[] definitions =
    [
        new(
            "Visual Studio Code",
            [
                User("{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1"),
                User("{D628A17A-9713-46BF-8D57-E671B46A741E}_is1"),
                User("{D9E514E7-1A56-452D-9337-2990C0DC4310}_is1"),
                Machine("{EA457B21-F73E-494C-ACAB-524FDE069978}_is1"),
                Machine32("{F8A2A208-72B3-4D61-95FC-8A65D340689B}_is1"),
                Machine("{A5270FC5-65AD-483E-AC30-2C276B63D0AC}_is1")
            ],
            ["Microsoft Visual Studio Code"],
            ["Microsoft Corporation"],
            "Code.exe"),
        new(
            "Visual Studio Code (Insiders)",
            [
                User("{217B4C08-948D-4276-BFBB-BEE930AE5A2C}_is1"),
                User("{26F4A15E-E392-4887-8C09-7BC55712FD5B}_is1"),
                User("{69BD8F7B-65EB-4C6F-A14E-44CFA83712C0}_is1"),
                Machine("{1287CAD5-7C8D-410D-88B9-0D1EE4A83FF2}_is1"),
                Machine32("{C26E74D1-022E-4238-8B9D-1E7564A36CC9}_is1"),
                Machine("{0AEDB616-9614-463B-97D7-119DD86CCA64}_is1")
            ],
            ["Microsoft Visual Studio Code Insiders"],
            ["Microsoft Corporation"],
            "Code - Insiders.exe"),
        new(
            "VSCodium",
            [
                User("{2E1F05D1-C245-4562-81EE-28188DB6FD17}_is1"),
                User("{0FD05EB4-651E-4E78-A062-515204B47A3A}_is1"),
                User("{57FD70A5-1B8D-4875-9F40-C5553F094828}_is1"),
                Machine("{88DA3577-054F-4CA1-8122-7D820494CFFB}_is1"),
                Machine32("{763CBF88-25C6-4B10-952F-326AE657F16B}_is1"),
                Machine("{67DEE444-3D04-4258-B92A-BC1F0FF2CAE4}_is1"),
                User("{C6065F05-9603-4FC4-8101-B9781A25D88E}}_is1"),
                User("{3AEBF0C8-F733-4AD4-BADE-FDB816D53D7B}_is1"),
                Machine("{D77B7E06-80BA-4137-BCF4-654B95CCEBC5}_is1"),
                Machine32("{E34003BB-9E10-4501-8C11-BE3FAA83F23F}_is1"),
                Machine("{D1ACE434-89C5-48D1-88D3-E2991DF85475}_is1")
            ],
            ["VSCodium"],
            ["VSCodium", "Microsoft Corporation"],
            "VSCodium.exe"),
        new(
            "Cursor",
            [
                User("62625861-8486-5be9-9e46-1da50df5f8ff"),
                User("{DADADADA-ADAD-ADAD-ADAD-ADADADADADAD}}_is1"),
                User("{DBDBDBDB-BDBD-BDBD-BDBD-BDBDBDBDBDBD}}_is1")
            ],
            ["Cursor", "Cursor (User)"],
            ["Cursor AI, Inc.", "Anysphere"],
            null),
        new(
            "Windsurf",
            [User("{5A8B7D94-9B5F-4D1F-93FC-5609F7159349}_is1")],
            ["Windsurf", "Windsurf (User)"],
            ["Codeium"],
            null),
        new(
            "Zed",
            [User("{2DB0DA96-CA55-49BB-AF4F-64AF36A86712}_is1")],
            ["Zed"],
            ["Zed Industries"],
            null),
        new(
            "Sublime Text",
            [Machine("Sublime Text_is1"), Machine("Sublime Text 3_is1")],
            ["Sublime Text"],
            ["Sublime HQ Pty Ltd"],
            "subl.exe"),
        new(
            "Notepad++",
            [Machine("Notepad++"), Machine32("Notepad++")],
            ["Notepad++"],
            ["Notepad++ Team"],
            null)
    ];

    public static Editor Notepad { get; } = new(
        "Notepad",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"));

    /// <summary>
    /// The editors installed on this machine, in the order listed above, then Notepad.
    /// </summary>
    public static List<Editor> Find() =>
        Find(ReadRegistry, File.Exists);

    /// <summary>
    /// <paramref name="readValues"/> gives a key's string values, or null when there is no such
    /// key. Passed in so tests can describe the machine.
    /// </summary>
    internal static List<Editor> Find(
        Func<RegistryHive, string, IReadOnlyDictionary<string, string>?> readValues,
        Func<string, bool> fileExists)
    {
        var found = new List<Editor>();
        foreach (var definition in definitions)
        {
            if (FindPath(definition, readValues, fileExists) is { } path)
            {
                found.Add(new(definition.Name, path));
            }
        }

        found.Add(Notepad);
        return found;
    }

    static string? FindPath(
        Definition definition,
        Func<RegistryHive, string, IReadOnlyDictionary<string, string>?> readValues,
        Func<string, bool> fileExists)
    {
        foreach (var (hive, subKey) in definition.Keys)
        {
            var values = readValues(hive, subKey);
            if (values == null)
            {
                continue;
            }

            var displayName = values.GetValueOrDefault("DisplayName", "");
            var publisher = values.GetValueOrDefault("Publisher", "");
            if (!definition.DisplayNamePrefixes.Any(_ => displayName.StartsWith(_, StringComparison.Ordinal)) ||
                !definition.Publishers.Contains(publisher))
            {
                continue;
            }

            var path = ExecutablePath(definition, values);
            if (path != null &&
                fileExists(path))
            {
                return path;
            }
        }

        return null;
    }

    static string? ExecutablePath(Definition definition, IReadOnlyDictionary<string, string> values)
    {
        if (definition.Executable == null)
        {
            // "C:\Path\app.exe",0 : the path, without the icon index or quotes.
            if (!values.TryGetValue("DisplayIcon", out var icon) || icon.Length == 0)
            {
                return null;
            }

            return icon.Split(',')[0].Replace("\"", "");
        }

        if (!values.TryGetValue("InstallLocation", out var folder) || folder.Length == 0)
        {
            return null;
        }

        return Path.Combine(folder, definition.Executable);
    }

    static IReadOnlyDictionary<string, string>? ReadRegistry(RegistryHive hive, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey);
            if (key == null)
            {
                return null;
            }

            var values = new Dictionary<string, string>();
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string value)
                {
                    values[name] = value;
                }
            }

            return values;
        }
        catch (Exception)
        {
            // An unreadable key is the same as no editor there.
            return null;
        }
    }

    /// <summary>
    /// The editor to use: the one chosen in Options while it is still there, otherwise the
    /// first one installed, otherwise Notepad.
    /// </summary>
    public static string EditorPath(Settings settings)
    {
        if (settings.TextEditor is { } chosen &&
            File.Exists(chosen))
        {
            return chosen;
        }

        return Find()[0].Path;
    }

    public static void Open(IWin32Window owner, Settings settings, IReadOnlyList<string> paths) =>
        Start(owner, EditorPath(settings), paths, paths[0]);

    /// <summary>
    /// Opens <paramref name="path"/> with the cursor at <paramref name="line"/> and
    /// <paramref name="column"/>, from Find in Files' results, as the editor is told to.
    /// </summary>
    public static void OpenAt(IWin32Window owner, Settings settings, string path, int line, int column)
    {
        var editor = EditorPath(settings);
        Start(owner, editor, AtLine(editor, path, line, column), path);
    }

    /// <summary>
    /// What an editor is given to open a file at a line and column: VS Code and those made from
    /// it take <c>-g file:line:column</c>, Sublime Text and Zed <c>file:line:column</c>, and
    /// Notepad++ <c>-n</c> and <c>-c</c>. Notepad, or an editor Lode doesn't know, the file alone.
    /// </summary>
    internal static IReadOnlyList<string> AtLine(string editor, string path, int line, int column)
    {
        if (IsCodeFamily(editor))
        {
            return ["-g", $"{path}:{line}:{column}"];
        }

        var name = Path.GetFileNameWithoutExtension(editor);

        if (name.Equals("subl", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sublime_text", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Zed", StringComparison.OrdinalIgnoreCase))
        {
            return [$"{path}:{line}:{column}"];
        }

        if (name.Equals("notepad++", StringComparison.OrdinalIgnoreCase))
        {
            return [$"-n{line}", $"-c{column}", path];
        }

        return [path];
    }

    /// <summary>
    /// What an editor is given to show two files side by side, the changes between them marked:
    /// VS Code and those made from it take <c>--diff left right</c>. Null for an editor that
    /// can't, which is given the right one alone.
    /// </summary>
    internal static IReadOnlyList<string>? Diff(string editor, string left, string right)
    {
        if (IsCodeFamily(editor))
        {
            return ["--diff", left, right];
        }

        return null;
    }

    /// <summary>
    /// Shows the changes from <paramref name="left"/> to <paramref name="right"/> in the editor
    /// chosen in Options. False when it can't show changes, for the caller to find another way.
    /// </summary>
    public static bool OpenDiff(IWin32Window owner, Settings settings, string left, string right)
    {
        var editor = EditorPath(settings);
        if (Diff(editor, left, right) is not { } arguments)
        {
            return false;
        }

        Start(owner, editor, arguments, right);
        return true;
    }

    static bool IsCodeFamily(string editor)
    {
        var name = Path.GetFileNameWithoutExtension(editor);
        return name.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("VSCodium", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Cursor", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Windsurf", StringComparison.OrdinalIgnoreCase);
    }

    static void Start(IWin32Window owner, string editor, IReadOnlyList<string> arguments, string first)
    {
        try
        {
            var startInfo = new ProcessStartInfo(editor)
            {
                UseShellExecute = false,

                // Some editors are started through a console program, Sublime Text's subl.exe,
                // which hands the files to the editor and exits. Without this, Windows gives it a
                // console window of its own, which flashes up while it runs.
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(first) ?? ""
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception)
        {
            MessageBoxes.Show(owner, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Common text formats whose type Windows may not know as text.
    static HashSet<string> textExtensions =
    [
        with(StringComparer.OrdinalIgnoreCase),
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv",
        ".json", ".jsonc", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".config", ".env",
        ".editorconfig", ".gitignore", ".gitattributes",
        ".cs", ".csx", ".csproj", ".props", ".targets", ".sln", ".slnx", ".vb", ".fs", ".fsx",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".less", ".html", ".htm",
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".swift", ".php", ".sql",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".svg"
    ];

    /// <summary>
    /// True for a file to offer Edit on: a known text extension, or one Windows says is text.
    /// </summary>
    public static bool IsText(string path) =>
        IsText(path, RememberedPerceivedType);

    // What Windows said each extension is. Asking reads the registry, and the actions bar asks
    // about every file selected each time the selection changes.
    static ConcurrentDictionary<string, string?> perceivedTypes = new(StringComparer.OrdinalIgnoreCase);

    static string? RememberedPerceivedType(string extension) =>
        perceivedTypes.GetOrAdd(extension, PerceivedType);

    /// <summary>
    /// Asks Windows again what each type of file is, for Refresh: a program installed since may
    /// have changed the answer.
    /// </summary>
    public static void ForgetTypes() =>
        perceivedTypes.Clear();

    internal static bool IsText(string path, Func<string, string?> perceivedType)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return false;
        }

        return textExtensions.Contains(extension) ||
               string.Equals(perceivedType(extension), "text", StringComparison.OrdinalIgnoreCase);
    }

    static string? PerceivedType(string extension)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extension);
            return key?.GetValue("PerceivedType") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
