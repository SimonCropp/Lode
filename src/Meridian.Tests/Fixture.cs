/// <summary>
/// A sample folder with fixed names, sizes, attributes and dates, so that what the app shows
/// for it is the same on every run. The extensions are made up so no installed program gives
/// them its own icon or type name.
/// </summary>
sealed class Fixture : IDisposable
{
    static readonly DateTime baseline = new(2025, 3, 14, 9, 26, 0);

    readonly string container;

    public string Root { get; }

    Fixture(string container)
    {
        this.container = container;
        Root = Path.Combine(container, "Projects");
        if (Directory.Exists(container))
        {
            Directory.Delete(container, true);
        }

        Build();
    }

    /// <summary>
    /// A fixture in the temp folder, one per test so tests can run side by side.
    /// </summary>
    public static Fixture Create([CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var name = $"{Path.GetFileNameWithoutExtension(file)}.{member}";
        return new(Path.Combine(Path.GetTempPath(), "MeridianTests", name));
    }

    /// <summary>
    /// A fixture at a path that is the same on every Windows machine, for captures: the
    /// address bar shows the full path, so a per-user temp folder would end up in the image.
    /// Only one of these can exist at a time.
    /// </summary>
    public static Fixture CreateAtFixedPath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        return new(Path.Combine(documents, "MeridianTests"));
    }

    void Build()
    {
        Directory.CreateDirectory(At("Archive"));
        Directory.CreateDirectory(At("Cache"));
        Directory.CreateDirectory(At("Docs"));
        Directory.CreateDirectory(At("Source", "Deep"));
        Write(0, "summary.gamma");
        Write(12, "notes.alpha");
        Write(64, "hidden.alpha");
        Write(2_048, "report2.beta");
        Write(300_000, "report10.beta");
        Write(1_500_000, "Report1.beta");
        Write(900, "Docs", "guide.alpha");
        Write(5_000, "Source", "main.alpha");
        Write(40, "Source", "Deep", "leaf.alpha");

        File.SetAttributes(At("hidden.alpha"), FileAttributes.Hidden);
        File.SetAttributes(At("Cache"), FileAttributes.Directory | FileAttributes.Hidden);

        // Last, because creating a child touches its parent's date.
        var paths = Directory
            .EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();
        for (var index = 0; index < paths.Count; index++)
        {
            var time = baseline.AddHours(index * 31);
            if (Directory.Exists(paths[index]))
            {
                Directory.SetLastWriteTime(paths[index], time);
            }
            else
            {
                File.SetLastWriteTime(paths[index], time);
            }
        }

        Directory.SetLastWriteTime(Root, baseline);
    }

    void Write(int size, params string[] parts) =>
        File.WriteAllBytes(At(parts), new byte[size]);

    public string At(params string[] parts) =>
        Path.Combine([Root, .. parts]);

    /// <summary>
    /// A path as it reads from the fixture's container, so snapshots don't hold temp paths.
    /// </summary>
    public string Relative(string path) =>
        Path.GetRelativePath(container, path).Replace('\\', '/');

    /// <summary>
    /// Every file and folder currently on disk, for asserting what a file operation did.
    /// </summary>
    public List<string> Tree() =>
        Directory
            .EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Select(_ => Directory.Exists(_) ? Relative(_) + "/" : $"{Relative(_)} ({new FileInfo(_).Length:N0} bytes)")
            .Order(StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        try
        {
            Directory.Delete(container, true);
        }
        catch (IOException)
        {
            // Something still has a handle open; the next run clears it.
        }
    }
}
