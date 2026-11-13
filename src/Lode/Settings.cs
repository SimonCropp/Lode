sealed class Settings
{
    static JsonSerializerOptions serializerOptions = new()
    {
        WriteIndented = true
    };

    // Null for settings that live only in memory, which is what tests use.
    string? filePath;

    public bool ShowHidden { get; set; } = true;

    // The tree's Recent section: folders Explorer adds to Quick access because they are used often.
    public bool ShowRecent { get; set; } = true;

    // The file list's columns hidden from its header's right-click menu: Date, Type or Size.
    public List<string> HiddenColumns { get; set; } = [];

    // The tree's sections (Pinned, Recent) the user collapsed, so they stay that way.
    public List<string> CollapsedSections { get; set; } = [];

    // Where a new window opens when it is given nothing else to show. Null for the user folder.
    public string? DefaultDirectory { get; set; }

    // The executable Edit opens text files in. Null for the first editor installed, or Notepad.
    public string? TextEditor { get; set; }

    // The folder holding the user's git repositories. Folders in it get git and NuGet commands
    // on their right-click menu. Null for none.
    public string? CodeDirectory { get; set; }

    /// <summary>
    /// True when <paramref name="folder"/> is the code folder or inside it.
    /// </summary>
    public bool IsCode(string folder) =>
        CodeDirectory != null &&
        !Locations.IsThisPC(folder) &&
        Locations.IsWithin(folder, CodeDirectory);

    /// <summary>
    /// <see cref="DefaultDirectory"/> when it is set and still there, otherwise the user folder.
    /// </summary>
    public string StartLocation()
    {
        if (DefaultDirectory != null &&
            (Locations.IsThisPC(DefaultDirectory) || Directory.Exists(DefaultDirectory)))
        {
            return DefaultDirectory;
        }

        return Locations.Home;
    }

    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public int SplitterDistance { get; set; }
    public List<string> Tabs { get; set; } = [];
    public int ActiveTab { get; set; }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lode",
            "settings.json");

    public static Settings Load(string path)
    {
        Settings? settings = null;
        try
        {
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            }
        }
        catch (Exception)
        {
            // A corrupt settings file should never stop the app from starting.
        }

        settings ??= new();
        settings.filePath = path;
        return settings;
    }

    public void Save()
    {
        if (filePath == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(this, serializerOptions));
        }
        catch (Exception)
        {
            // Settings are a convenience; failing to persist them is not fatal.
        }
    }
}
