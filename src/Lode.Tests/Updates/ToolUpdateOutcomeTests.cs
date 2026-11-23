/// <summary>
/// The report an update leaves for the Lode it starts again. Ported from PlatformHeads' tests,
/// with ToolUpdateOutcome.
/// </summary>
public class ToolUpdateOutcomeTests
{
    // What dotnet tool update wrote when a running process held the old version's files.
    const string locked =
        """
        failed
        Tool 'lode' failed to update due to the following:
        Failed to uninstall tool package 'lode': Access to the path 'C:\Users\me\.dotnet\tools\.store\lode\0.1.0' is denied.
        """;

    // Windows PowerShell writes line by line with a carriage return. A file with no status line
    // is the failure it could only have been, rather than a success with a stray first line.
    [Test]
    public Task Parse() =>
        Verify(
            new
            {
                succeeded = Describe(ToolUpdateOutcome.Parse("ok\nTool 'lode' was successfully updated from version '0.1.0' to version '0.1.1'.\n")),
                failed = Describe(ToolUpdateOutcome.Parse(locked)),
                windowsLineEndings = Describe(ToolUpdateOutcome.Parse("failed\r\nTool 'lode' failed to update.\r\nIt did not work.\r\n")),
                noStatusLine = Describe(ToolUpdateOutcome.Parse("Tool 'lode' failed to update due to the following:\nIt did not work.\n")),
                saidNothing = Describe(ToolUpdateOutcome.Parse("failed\n\n"))
            })
            .Snapshot(
                """
                {
                  succeeded: {
                    Succeeded: true,
                    Reason: Tool 'lode' was successfully updated from version '0.1.0' to version '0.1.1'.
                  },
                  failed: {
                    Succeeded: false,
                    Reason: Failed to uninstall tool package 'lode': Access to the path 'C:\Users\me\.dotnet\tools\.store\lode\0.1.0' is denied.
                  },
                  windowsLineEndings: {
                    Succeeded: false,
                    Reason: It did not work.
                  },
                  noStatusLine: {
                    Succeeded: false,
                    Reason: It did not work.
                  },
                  saidNothing: {
                    Succeeded: false,
                    Reason: dotnet tool update failed without saying why.
                  }
                }
                """);

    [Test]
    public async Task TakenOnceThenRemoved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Lode.Outcome.{Guid.NewGuid():N}.log");
        try
        {
            // As Windows PowerShell's Set-Content -Encoding UTF8 writes it, with a byte order mark.
            await File.WriteAllTextAsync(path, locked, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var taken = ToolUpdateOutcome.Take(path)!;

            await Verify(
                new
                {
                    taken = Describe(taken),
                    removed = !File.Exists(path),
                    noneAgain = ToolUpdateOutcome.Take(path) == null
                })
                .Snapshot(
                    """
                    {
                      taken: {
                        Succeeded: false,
                        Reason: Failed to uninstall tool package 'lode': Access to the path 'C:\Users\me\.dotnet\tools\.store\lode\0.1.0' is denied.
                      },
                      removed: true,
                      noneAgain: true
                    }
                    """);
        }
        finally
        {
            File.Delete(path);
        }
    }

    static object Describe(ToolUpdateOutcome outcome) =>
        new
        {
            outcome.Succeeded,
            outcome.Reason
        };
}
