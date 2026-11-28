/// <summary>
/// Extracting a flat zip, of 20,000 small files all at its top, as a zip of photos or logs is:
/// Here into the folder it is in, as Extract here does, which says what it put at the top, for
/// selecting, and what it made, for undo; All to a new folder of its own, as Extract All does,
/// which uses neither. Each is extracted to a folder emptied before it. The zip is made once, in
/// the temp folder, and kept for the next run.
/// </summary>
[MemoryDiagnoser]
public class ZipBenchmarks
{
    const int files = 20_000;

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "Zip");

    string zip = "";
    string here = "";
    string all = "";

    [GlobalSetup]
    public void Setup()
    {
        zip = Path.Combine(root, "flat.zip");
        here = Path.Combine(root, "Here");
        all = Path.Combine(root, "All");
        var ready = Path.Combine(root, "ready");
        if (!File.Exists(ready))
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                for (var index = 0; index < files; index++)
                {
                    using var writer = new StreamWriter(archive.CreateEntry($"photo-{index:00000}.jpg").Open());
                    writer.Write($"photo {index}");
                }
            }

            File.WriteAllText(ready, "");
        }

        Empty();
    }

    [IterationSetup]
    public void Empty()
    {
        foreach (var folder in new[] {here, all})
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
        }
    }

    // How many it put at the top, so all of it is used.
    [Benchmark]
    public int Here() =>
        Zip.Extract(zip, here, null, Cancel.None).TopLevel.Count;

    [Benchmark]
    public int All() =>
        Zip.ExtractTo(zip, all, false, null, Cancel.None).TopLevel.Count;
}
