public class FileOperationsTests
{
    [Test]
    public async Task CopyToAnotherFolder()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("report2.beta"), fixture.At("Source")];

        await FileOperations.Transfer(IntPtr.Zero, sources, fixture.At("Archive"), move: false);

        await Verify(fixture.Tree());
    }

    [Test]
    public async Task CopyIntoSameFolderMakesDuplicate()
    {
        using var fixture = Fixture.Create();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("notes.aleph")], fixture.Root, move: false);

        await Verify(fixture.Tree());
    }

    [Test]
    public async Task MoveToAnotherFolder()
    {
        using var fixture = Fixture.Create();
        string[] sources = [fixture.At("notes.aleph"), fixture.At("Docs")];

        await FileOperations.Transfer(IntPtr.Zero, sources, fixture.At("Archive"), move: true);

        await Verify(fixture.Tree());
    }

    [Test]
    public async Task MoveIntoSameFolderChangesNothing()
    {
        using var fixture = Fixture.Create();

        await FileOperations.Transfer(IntPtr.Zero, [fixture.At("notes.aleph")], fixture.Root, move: true);

        await Verify(fixture.Tree());
    }

    // Delete has no test: recycling would leave files in the Recycle Bin of whoever ran the
    // tests, and a permanent delete through the shell stops to ask for confirmation.

    [Test]
    public async Task Rename()
    {
        using var fixture = Fixture.Create();

        var file = FileOperations.Rename(IntPtr.Zero, fixture.At("notes.aleph"), "renamed.aleph");
        var caseOnly = FileOperations.Rename(IntPtr.Zero, fixture.At("report2.beta"), "REPORT2.BETA");
        var folder = FileOperations.Rename(IntPtr.Zero, fixture.At("Docs"), "Documents");

        await Verify(
            new
            {
                file,
                caseOnly,
                folder,
                tree = fixture.Tree()
            });
    }

    [Test]
    public async Task CreateFolderPicksFreeName()
    {
        using var fixture = Fixture.Create();

        string[] created =
        [
            FileOperations.CreateFolder(fixture.Root),
            FileOperations.CreateFolder(fixture.Root),
            FileOperations.CreateFolder(fixture.Root)
        ];

        await Verify(created.Select(fixture.Relative));
    }

    [Test]
    public async Task CreateFilePicksFreeNameAndWritesData()
    {
        using var fixture = Fixture.Create();
        var empty = new ShellNew.Entry(".txt", "Text Document");
        var withData = new ShellNew.Entry(".rtf", "Rich Text Document", @"{\rtf1}"u8.ToArray());

        string[] created =
        [
            FileOperations.CreateFile(fixture.Root, empty),
            FileOperations.CreateFile(fixture.Root, empty),
            FileOperations.CreateFile(fixture.Root, withData)
        ];

        await Verify(created.Select(_ => new
        {
            path = fixture.Relative(_),
            content = File.ReadAllText(_)
        }));
    }
}
