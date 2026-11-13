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
                share = Describe(@"\\server\share\folder")
            });

    static IEnumerable<string> Describe(string location) =>
        Breadcrumb.Segments(location).Select(_ => $"{_.Path} => {_.Name}");
}
