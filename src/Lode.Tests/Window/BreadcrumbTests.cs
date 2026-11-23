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
                server = Describe(@"\\server"),
                // A drive Windows has a name for, as the address bar asks for them.
                named = Describe(@"D:\Projects", _ => "Data (D:)")
            })
            .Snapshot(
                """
                {
                  folder: [
                    This PC => This PC,
                    D:\ => D:,
                    D:\Projects => Projects,
                    D:\Projects\Lode => Lode,
                    D:\Projects\Lode\src => src
                  ],
                  drive: [
                    This PC => This PC,
                    D:\ => D:
                  ],
                  thisPC: [
                    This PC => This PC
                  ],
                  share: [
                    \\server => server,
                    \\server\share => share,
                    \\server\share\folder => folder
                  ],
                  server: [
                    \\server => server
                  ],
                  named: [
                    This PC => This PC,
                    D:\ => Data (D:),
                    D:\Projects => Projects
                  ]
                }
                """);

    static IEnumerable<string> Describe(string location, Func<string, string>? driveName = null) =>
        Breadcrumb.Segments(location, driveName).Select(_ => $"{_.Path} => {_.Name}");
}