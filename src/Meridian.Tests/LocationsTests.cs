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
        "This PC",
        "this pc",
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
    public Task IsWithin()
    {
        (string Path, string Folder)[] pairs =
        [
            (@"C:\Code\Meridian", @"C:\Code"),
            (@"C:\Code", @"C:\Code"),
            (@"C:\Code\", @"C:\Code"),
            (@"c:\code\meridian", @"C:\Code"),
            (@"C:\CodeReview", @"C:\Code"),
            (@"C:\Code", @"C:\Code\Meridian"),
            (@"C:\Code", @"C:\"),
            (@"D:\Code", @"C:\")
        ];
        return Verify(pairs.ToDictionary(_ => $"{_.Path} in {_.Folder}", _ => Locations.IsWithin(_.Path, _.Folder)));
    }

    static IEnumerable<string> Normalized() =>
        inputs
            .Select(Locations.Normalize)
            .OfType<string>()
            .Distinct();
}
