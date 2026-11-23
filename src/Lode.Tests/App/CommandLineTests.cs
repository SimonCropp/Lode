public class CommandLineTests
{
    static readonly string[][] inputs =
    [
        [],
        [@"D:\Code"],
        [@"D:\Code", @"D:\Code\readme.md"],
        ["/temp"],
        // Control Panel, then This PC, as the explore verb on Folder names them.
        ["::{26EE0668-A00A-44D7-9371-BEB064C98683}"],
        [@"D:\Code", "::{645FF040-5081-101B-9F08-00AA002F954E}"],
        ["::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"],
        ["--replace-explorer"],
        ["--RESTORE-EXPLORER"],
        ["--help"],
        ["-h"],
        ["/?"],
        ["--bogus"],
        ["--replace-explorer", @"D:\Code"],
        ["--replace-explorer", "--restore-explorer"]
    ];

    [Test]
    public Task Parse() =>
        Verify(
            inputs.ToDictionary(
                _ =>
                {
                    if (_.Length == 0)
                    {
                        return "(none)";
                    }

                    return string.Join(' ', _.Select(argument => $"[{argument}]"));
                },
                CommandLine.Parse))
                .Snapshot(
                    """
                    {
                      (none): {},
                      [--bogus]: {
                        Kind: Help,
                        Error: Unknown option: --bogus
                      },
                      [--help]: {
                        Kind: Help
                      },
                      [--replace-explorer]: {
                        Kind: ReplaceExplorer
                      },
                      [--replace-explorer] [--restore-explorer]: {
                        Kind: Help,
                        Error: Pass one switch on its own, without folders or files.
                      },
                      [--replace-explorer] [D:\Code]: {
                        Kind: Help,
                        Error: Pass one switch on its own, without folders or files.
                      },
                      [--RESTORE-EXPLORER]: {
                        Kind: RestoreExplorer
                      },
                      [-h]: {
                        Kind: Help
                      },
                      [/?]: {
                        Kind: Help
                      },
                      [/temp]: {
                        Paths: [
                          /temp
                        ]
                      },
                      [::{20D04FE0-3AEA-1069-A2D8-08002B30309D}]: {
                        Paths: [
                          ::{20D04FE0-3AEA-1069-A2D8-08002B30309D}
                        ]
                      },
                      [::{26EE0668-A00A-44D7-9371-BEB064C98683}]: {
                        ForExplorer: [
                          ::{26EE0668-A00A-44D7-9371-BEB064C98683}
                        ]
                      },
                      [D:\Code]: {
                        Paths: [
                          D:\Code
                        ]
                      },
                      [D:\Code] [::{645FF040-5081-101B-9F08-00AA002F954E}]: {
                        Paths: [
                          D:\Code
                        ],
                        ForExplorer: [
                          ::{645FF040-5081-101B-9F08-00AA002F954E}
                        ]
                      },
                      [D:\Code] [D:\Code\readme.md]: {
                        Paths: [
                          D:\Code,
                          D:\Code\readme.md
                        ]
                      }
                    }
                    """);

    // A relative path is from the folder the launch was started in. A copy of Lode already
    // running was started somewhere else, so it is handed the full path.
    [Test]
    public async Task RelativePathsAreMadeFull()
    {
        var started = Environment.CurrentDirectory;
        var commandLine = CommandLine.Parse([".", @"Source\Deep", @"D:\Code", Locations.ThisPC]);

        await Assert.That(commandLine.FullPaths()).IsEquivalentTo(
            [started, Path.Combine(started, "Source", "Deep"), @"D:\Code", Locations.ThisPC],
            CollectionOrdering.Matching);
    }
}
