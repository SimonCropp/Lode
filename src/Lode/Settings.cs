using System.Text.Json.Serialization;

sealed class Settings
{
    static JsonSerializerOptions serializerOptions = new()
    {
        WriteIndented = true
    };

    // Null for settings that live only in memory, which is what tests use.
    string? filePath;

    public bool ShowHidden { get; set; } = true;

    // Windows' own files and folders, Program Files, and other people's folders. See SystemItems.
    public bool ShowSystem { get; set; }

    /// <summary>
    /// What to leave out of listings for Show system files, or null when it is on.
    /// </summary>
    public SystemItems? HiddenSystemItems()
    {
        if (ShowSystem)
        {
            return null;
        }

        return SystemItems.ForThisMachine;
    }

    // Light or dark, or whichever Windows is set to. See Theme.
    public Theme Theme { get; set; }

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

    // Whether an action shown as a button is in its parent's drop-down on the bar, rather than a
    // button of its own.
    public bool NestsButton(string id)
    {
        if (Actions.TryGetValue(id, out var placement) &&
            placement.ButtonNested is { } nested)
        {
            return nested;
        }

        return false;
    }

    // Whether an action in the right-click menus is in its parent's submenu, rather than an
    // entry of its own.
    public bool NestsInMenu(string id)
    {
        if (Actions.TryGetValue(id, out var placement) &&
            placement.MenuNested is { } nested)
        {
            return nested;
        }

        return LodeActions.Find(id)?.MenuNested ?? false;
    }

    // Git was one action, a submenu of pull, push, discard and purge, which are now actions of
    // their own under Git. Where Git was placed, each of them is, nested, so the menu and the bar
    // are as they were.
    void SplitGit()
    {
        if (!Actions.Remove("git", out var git))
        {
            return;
        }

        foreach (var id in LodeActions.GitIds)
        {
            Actions.TryAdd(
                id,
                new()
                {
                    Button = git.Button,
                    Menu = git.Menu,
                    ButtonNested = true,
                    MenuNested = true
                });
        }
    }

    /// <param name="buttonNested">Null for LodeActions' default, as for an action without a parent.</param>
    /// <param name="menuNested">Null for LodeActions' default, as for an action without a parent.</param>
    public void SetPlacement(string id, bool button, bool menu, bool? buttonNested = null, bool? menuNested = null) =>
        Actions[id] = new()
        {
            Button = button,
            Menu = menu,
            ButtonNested = buttonNested,
            MenuNested = menuNested
        };

    // The file list's columns hidden from its header's right-click menu: Date, Type or Size.
    public List<string> HiddenColumns { get; set; } = [];

    // The tree's sections (Pinned, Recent) the user collapsed, so they stay that way.
    public List<string> CollapsedSections { get; set; } = [];

    // What Quick access had pinned, and its recent folders, when it was last read. A window
    // opens with these in its tree, rather than waiting for Quick access to be read. See Places.
    public List<string> PinnedFolders { get; set; } = [];
    public List<string> RecentFolders { get; set; } = [];

    // The folders removed from the taskbar's jump list, which it leaves out while Quick access
    // has them. See JumpList.
    public List<string> JumpListRemoved { get; set; } = [];

    // Where a new window opens when it is given nothing else to show. Null for the user folder.
    public string? DefaultDirectory { get; set; }

    // The executable Edit opens text files in. Null for the first editor installed, or Notepad.
    public string? TextEditor { get; set; }

    // The folder holding the user's git repositories. Folders in it get git and NuGet commands
    // on their right-click menu. Null for none.
    public string? CodeDirectory { get; set; }

    // Git purge deletes what git doesn't track outright, rather than to the Recycle Bin.
    public bool PurgePermanently { get; set; }

    // When NuGet was last asked for a newer Lode, and the newer version it had then, if any.
    // See Updates.
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string? AvailableUpdate { get; set; }

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
            (Locations.IsThisPC(DefaultDirectory) || Locations.IsRemote(DefaultDirectory) || Directory.Exists(DefaultDirectory)))
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

    internal static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lode");

    // A Debug build keeps its own, so one run from an IDE beside the installed Lode doesn't
    // save its tabs and window over the installed one's.
    const string fileName =
#if DEBUG
        "settings.debug.json";
#else
        "settings.json";
#endif

    public static string DefaultPath =>
        Path.Combine(Folder, fileName);

    /// <summary>
    /// On a Debug build's first run, starts its settings as a copy of the installed Lode's, so it
    /// opens with the same options and tabs. Nothing once it has settings of its own.
    /// </summary>
    public static void SeedFromInstalled()
    {
        var installed = Path.Combine(Folder, "settings.json");
        if (DefaultPath == installed ||
            File.Exists(DefaultPath) ||
            !File.Exists(installed))
        {
            return;
        }

        try
        {
            File.Copy(installed, DefaultPath);
        }
        catch (IOException)
        {
            // Starting with defaults is fine.
        }
    }

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
        settings.SplitGit();
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
/// Whether an action shows as a button on the actions bar, in the right-click menu, or both, and
/// in each, whether under its parent.
/// </summary>
sealed class ActionPlacement
{
    public bool Button { get; set; }
    public bool Menu { get; set; }

    // Null takes LodeActions' default, as a placement saved before actions had parents does.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ButtonNested { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MenuNested { get; set; }
}
