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
                CommandLine.Parse));
}
