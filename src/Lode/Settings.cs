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

    // Where each action shows, for those changed in Options: as a button on the actions bar, in
    // the right-click menu, both or neither. Those missing show where LodeActions says.
    public Dictionary<string, ActionPlacement> Actions { get; set; } = [];

    public bool ShowsAsButton(string id)
    {
        if (Actions.TryGetValue(id, out var placement))
        {
            return placement.Button;
        }

        return LodeActions.Find(id)?.Button ?? false;
    }

    public bool ShowsInMenu(string id)
    {
        if (Actions.TryGetValue(id, out var placement))
        {
            return placement.Menu;
        }

        return LodeActions.Find(id)?.Menu ?? true;
    }

    public void SetPlacement(string id, bool button, bool menu) =>
        Actions[id] = new()
        {
            Button = button,
            Menu = menu
        };

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

/// <summary>
/// Whether an action shows as a button on the actions bar, in the right-click menu, or both.
/// </summary>
sealed class ActionPlacement
{
    public bool Button { get; set; }
    public bool Menu { get; set; }
}
