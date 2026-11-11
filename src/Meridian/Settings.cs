using System.Text.Json;

sealed class Settings
{
    static readonly JsonSerializerOptions serializerOptions = new() { WriteIndented = true };

    // Null for settings that live only in memory, which is what tests use.
    string? filePath;

    public bool ShowHidden { get; set; }
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
            "Meridian",
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
