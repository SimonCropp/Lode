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
        VerifyPaths(inputs.ToDictionary(_ => $"[{_}]", _ => Locations.Normalize(_) ?? "(rejected)"));

    // Typed in the address bar: a path that is not full is taken from the folder the tab shows,
    // not from Lode's working directory. Full ones, This PC, and names that are not paths in a
    // folder, are as they would be anyway.
    [Test]
    public Task NormalizeRelativeToAFolder() =>
        VerifyPaths(
            new[]
            {
                "src",
                @"src\Lode",
                "..",
                @"..\Other",
                ".",
                @"\Code",
                "cmd.exe",
                @"E:\Elsewhere",
                @"\\server\share",
                "This PC"
            }.ToDictionary(_ => $"[{_}]", _ => Locations.Normalize(_, @"D:\Code\Lode") ?? "(rejected)"));

    // Nor is a name that is not a path in the folder: it is as it would be without one.
    [Test]
    public async Task NormalizeRelativeToAFolderLeavesShellNames() =>
        await Assert.That(Locations.Normalize("shell:downloads", @"D:\Code\Lode")).IsEqualTo(Locations.Normalize("shell:downloads"));

    // At This PC, there is no folder to take it from.
    [Test]
    public async Task NormalizeRelativeToThisPC() =>
        await Assert.That(Locations.Normalize("src", Locations.ThisPC)).IsEqualTo(Locations.Normalize("src"));

    [Test]
    public Task Parent() =>
        VerifyPaths(Normalized().ToDictionary(_ => _, _ => Locations.Parent(_) ?? "(none)"));

    [Test]
    public Task DisplayName() =>
        VerifyPaths(Normalized().ToDictionary(_ => _, Locations.DisplayName));

    [Test]
    public Task Labels() =>
        VerifyPaths(
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
        return VerifyPaths(pairs.ToDictionary(_ => $"{_.Path} in {_.Folder}", _ => Locations.IsWithin(_.Path, _.Folder)));
    }

    [Test]
    public Task Moved()
    {
        (string Path, string From, string To)[] moves =
        [
            // The folder itself, renamed, and moved into another.
            (@"C:\Code\Lode", @"C:\Code\Lode", @"C:\Code\Lode2"),
            (@"C:\Code\Lode", @"C:\Code\Lode", @"D:\Archive\Lode"),
            // A folder inside it goes with it.
            (@"C:\Code\Lode\src\Lode", @"C:\Code\Lode", @"C:\Code\Lode2"),
            (@"c:\code\lode\src", @"C:\Code\Lode", @"C:\Code\Lode2"),
            (@"C:\Code\Lode\src\", @"C:\Code\Lode\", @"C:\Code\Lode2\"),
            // Only the case changed.
            (@"C:\Code\lode\src", @"C:\Code\lode", @"C:\Code\Lode"),
            // Beside it, or above it, it stays where it is.
            (@"C:\Code\LodeTools", @"C:\Code\Lode", @"C:\Code\Lode2"),
            (@"C:\Code", @"C:\Code\Lode", @"C:\Code\Lode2")
        ];
        return VerifyPaths(moves.ToDictionary(_ => $"{_.Path} with {_.From} -> {_.To}", _ => Locations.Moved(_.Path, _.From, _.To) ?? "(not within)"));
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

    // The paths as they are. Verify writes one that is a folder on the machine the tests run on,
    // where they are checked out or the temp folder, as {SolutionDirectory} or {TempPath}, so a
    // made-up path that happens to be one would read differently from machine to machine.
    static SettingsTask VerifyPaths(object? target) =>
        Verify(target).DisableScrubbers();

    static IEnumerable<string> Normalized() =>
        inputs
            .Select(_ => Locations.Normalize(_))
            .OfType<string>()
            .Distinct();
}
