public class CommandLineTests
{
    static readonly string[][] inputs =
    [
        [],
        [@"D:\Code"],
        [@"D:\Code", @"D:\Code\readme.md"],
        ["/temp"],
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
                _ => _.Length == 0 ? "(none)" : string.Join(' ', _.Select(argument => $"[{argument}]")),
                CommandLine.Parse));
}
