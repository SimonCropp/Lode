public class BreadcrumbTests
{
    // Each button as "its path => what it shows".
    [Test]
    public Task Segments() =>
        Verify(
            new
            {
                folder = Describe(@"D:\Projects\Lode\src"),
                drive = Describe(@"D:\"),
                thisPC = Describe(Locations.ThisPC),
                share = Describe(@"\\server\share\folder"),
                // A drive Windows has a name for, as the address bar asks for them.
                named = Describe(@"D:\Projects", _ => "Data (D:)")
            });

    static IEnumerable<string> Describe(string location, Func<string, string>? driveName = null) =>
        Breadcrumb.Segments(location, driveName).Select(_ => $"{_.Path} => {_.Name}");
}
