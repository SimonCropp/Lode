[STAThreadExecutor]
public class OpenWithTests
{
    // Open with lists apps for one extension at a time, and none for programs or shortcuts.
    [Test]
    public Task Extension() =>
        Verify(
            new Dictionary<string, string>
            {
                ["one file"] = Extension(@"C:\photo.png"),
                ["same type, any case"] = Extension(@"C:\photo.png", @"C:\other.PNG"),
                ["different types"] = Extension(@"C:\photo.png", @"C:\notes.txt"),
                ["no extension"] = Extension(@"C:\Makefile"),
                ["program"] = Extension(@"C:\setup.exe"),
                ["script"] = Extension(@"C:\build.cmd"),
                ["shortcut"] = Extension(@"C:\Notepad.lnk")
            });

    // Quoted, so no extension shows as "" rather than nothing.
    static string Extension(params string[] paths)
    {
        if (OpenWith.Extension(paths) is { } extension)
        {
            return $"\"{extension}\"";
        }

        return "no Open with";
    }

    // What is installed differs between machines, so only the shape is checked.
    [Test]
    public async Task FindsTheRecommendedAppsByName()
    {
        var names = OpenWith.Find(".txt").Select(_ => _.Name).ToList();

        await Assert.That(names.All(_ => _.Length > 0)).IsTrue();
        await Assert.That(names).IsEquivalentTo(names.Order(StringComparer.CurrentCultureIgnoreCase), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // The fixture's extensions are made up.
    [Test]
    public async Task NothingIsRecommendedForAnUnknownType() =>
        await Assert.That(OpenWith.Find(".aleph")).IsEmpty();

    // What an app is handed the files in.
    [Test]
    public async Task DataObjectForFiles()
    {
        using var fixture = Fixture.Create();

        var dataObject = ShellContextMenu.CreateDataObject(IntPtr.Zero, [fixture.At("notes.aleph"), fixture.At("summary.gamma")]);
        var created = dataObject != IntPtr.Zero;
        if (created)
        {
            Marshal.Release(dataObject);
        }

        await Assert.That(created).IsTrue();
    }
}
