/// <summary>
/// Undoing a delete of 200 items, every tenth a folder with a file in it, the rest files:
/// restoring them from the Recycle Bin to where they were. Before each undo the benchmark makes
/// them, in the temp folder, and sends them to the Recycle Bin itself, as Delete does; after
/// it, it restores any the undo left there, so it leaves the Recycle Bin as it found it.
/// </summary>
[MemoryDiagnoser]
// Each undo needs a delete before it: one undo is all an iteration can time.
[InvocationCount(1)]
public class RecycleBinBenchmarks
{
    const int items = 200;

    static readonly string root = Path.Combine(Path.GetTempPath(), "LodeBenchmarks", "RecycleBin");

    UndoHistory history = new(new UndoHistory.ShellFileActions());
    List<(string Original, string InBin)> deleted = [];

    [IterationSetup]
    public void Delete()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        Directory.CreateDirectory(root);
        var paths = new List<string>();
        for (var index = 0; index < items; index++)
        {
            var path = Path.Combine(root, $"Item{index:000}");
            if (index % 10 == 9)
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "inside.txt"), $"{index}");
            }
            else
            {
                path += ".txt";
                File.WriteAllText(path, $"{index}");
            }

            paths.Add(path);
        }

        var outcome = FileOperations.OnStaThread(
                () => ShellFileOperation.Run(
                    IntPtr.Zero,
                    ShellFileOperation.Kind.Delete,
                    paths,
                    null,
                    ShellFileOperation.AllowUndo | ShellFileOperation.RecycleOnDelete | ShellFileOperation.NoConfirmation))
            .Result;
        deleted = outcome.Changes
            .Where(_ => _.Result != null)
            .Select(_ => (_.Source, _.Result!))
            .ToList();
        if (deleted.Count != items)
        {
            PutBack();
            throw new($"Only {deleted.Count} of {items} items went to the Recycle Bin");
        }

        history = new(new UndoHistory.ShellFileActions());
        history.RecordDelete(deleted);
    }

    [Benchmark]
    public Task<string?> Undo() =>
        history.Undo(IntPtr.Zero);

    // Whatever the undo did, nothing the benchmark put in the Recycle Bin is left there.
    [IterationCleanup]
    public void PutBack()
    {
        var left = deleted
            .Select(_ => _.InBin)
            .Where(Path.Exists)
            .ToList();
        if (left.Count > 0)
        {
            FileOperations.OnStaThread(() => RecycleBin.Restore(IntPtr.Zero, left)).Wait();
        }

        deleted = [];
        if (left.Count > 0)
        {
            throw new($"The undo left {left.Count} of {items} items in the Recycle Bin");
        }
    }
}
