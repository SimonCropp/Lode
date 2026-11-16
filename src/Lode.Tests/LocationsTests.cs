public class LocationsTests
{
    static readonly string[] inputs =
    [
        @"C:\",
        "C:",
        // A drive opened through the replaced-Explorer command line, "%1" with %1 = C:\. The
        // backslash escapes the closing quote, so the argument arrives as C:".
        @"C:""",
        @"C:\Windows",
        @"C:\Windows\",
        """  "C:\Program Files\dotnet"  """,
        @"C:\Windows\System32\..\Fonts",
        "C:/Windows/System32",
        @"\\server\share",
        @"\\server\share\folder\",
        // A machine on the network, which lists its shares.
        @"\\server",
        @"\\server\",
        "This PC",
        "this pc",
        // This PC as the explore verb on Folder names it.
        "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        "",
        "   "
    ];

    [Test]
    public Task Normalize() =>
        Verify(inputs.ToDictionary(_ => $"[{_}]", _ => Locations.Normalize(_) ?? "(rejected)"));

    [Test]
    public Task Parent() =>
        Verify(Normalized().ToDictionary(_ => _, _ => Locations.Parent(_) ?? "(none)"));

    [Test]
    public Task DisplayName() =>
        Verify(Normalized().ToDictionary(_ => _, Locations.DisplayName));

    [Test]
    public Task Labels() =>
        Verify(
            Locations.Labels(
            [
                // Alone in its name.
                @"C:\Repos",
                // Told apart by the folder they are in.
                @"C:\Repos\Lode\src",
                @"C:\Repos\Other\src",
                // The same folder written another way is one folder.
                @"c:\repos\other\SRC",
                // Told apart only further up.
                @"C:\Repos\Lode\src\Lode",
                @"C:\Worktrees\Lode\src\Lode",
                // Told apart only by their roots. One in a share says which machine it is on.
                @"C:\Notes",
                @"D:\Notes",
                @"\\server\share\Notes",
                // Folders in a share say which machine they are on, also when told apart
                // further up.
                @"\\server\video\tv",
                @"\\server\backup\tv",
                @"\\server\video\movies",
                // Shares named alike, on two servers.
                @"\\server\share",
                @"\\other\share",
                // This PC beside a folder with its name.
                "This PC",
                @"C:\Backup\This PC"
            ]));

    [Test]
    public Task IsWithin()
    {
        (string Path, string Folder)[] pairs =
        [
            (@"C:\Code\Lode", @"C:\Code"),
            (@"C:\Code", @"C:\Code"),
            (@"C:\Code\", @"C:\Code"),
            (@"c:\code\lode", @"C:\Code"),
            (@"C:\CodeReview", @"C:\Code"),
            (@"C:\Code", @"C:\Code\Lode"),
            (@"C:\Code", @"C:\"),
            (@"D:\Code", @"C:\")
        ];
        return Verify(pairs.ToDictionary(_ => $"{_.Path} in {_.Folder}", _ => Locations.IsWithin(_.Path, _.Folder)));
    }

    // A share is on the network whatever is asked of it. The drive Windows is on is not, and
    // neither is somewhere with no drive at all.
    [Test]
    public async Task IsRemote()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        await Assert.That(Locations.IsRemote(@"\\server\share")).IsTrue();
        await Assert.That(Locations.IsRemote(@"\\server\share\folder\file.txt")).IsTrue();
        await Assert.That(Locations.IsRemote(windows)).IsFalse();
        await Assert.That(Locations.IsRemote(Path.GetPathRoot(windows)!)).IsFalse();
        await Assert.That(Locations.IsRemote(Locations.ThisPC)).IsFalse();
        await Assert.That(Locations.IsRemote("")).IsFalse();
    }

    static IEnumerable<string> Normalized() =>
        inputs
            .Select(_ => Locations.Normalize(_))
            .OfType<string>()
            .Distinct();
}
