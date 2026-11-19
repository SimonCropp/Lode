/// <summary>
/// What a search does with each batch it hands over: merges it, sorted, into everything found
/// so far, on the UI thread. That grows with how much has been found, which is why a search
/// holds what it finds longer as showing it comes to take longer (FolderView.SearchPace).
/// Sorted by Name, and by Folder, which makes each item's folder from its path for every
/// comparison.
/// </summary>
[MemoryDiagnoser]
public class SearchMergeBenchmarks
{
    const int batchSize = 2_000;

    [Params(100_000, 500_000, 1_000_000)]
    public int Found { get; set; }

    [Params("Name", "Folder")]
    public string Column { get; set; } = "Name";

    List<FileEntry> found = [];
    List<FileEntry> batch = [];
    Comparison<FileEntry> compare = null!;

    [GlobalSetup]
    public void Setup()
    {
        var column = FolderView.nameColumn;
        if (Column == "Folder")
        {
            column = FolderView.folderColumn;
        }

        compare = FolderView.Order(column, ascending: true);
        var random = new Random(42);
        found = Entries(Found, random);
        found.Sort(compare);
        batch = Entries(batchSize, random);
        batch.Sort(compare);
    }

    // Names as a code folder has them, numbered as people number things, in folders a few
    // levels down, so the logical comparison has digits to read.
    static List<FileEntry> Entries(int count, Random random)
    {
        var entries = new List<FileEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var folder = $@"C:\Code\Project{random.Next(200)}\src\Area{random.Next(50)}";
            var name = $"Item{random.Next(1_000_000)}.cs";
            entries.Add(new(name, $@"{folder}\{name}", false, 0, default, FileAttributes.Normal));
        }

        return entries;
    }

    // The count, as what was merged is Lode's own type, and so it is used.
    [Benchmark]
    public int Merge() =>
        FolderView.Merge(found, batch, compare).Count;
}
