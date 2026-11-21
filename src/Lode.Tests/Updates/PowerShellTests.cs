public class PowerShellTests
{
    // Read back by Windows PowerShell, which Lode's scripts run in, a quoted string is what was
    // quoted: a quote in it, straight or typographic, as in "St Mary’s", doesn't end it early.
    [Test]
    public async Task QuotedIsReadBackAsItWas()
    {
        string[] values =
        [
            "O'Brien",
            "St Mary’s College",
            "‘quoted’",
            "low‚ and ‛reversed",
            "$env:PATH; Remove-Item `x",
            "''"
        ];
        var script = $"""
                      [Console]::OutputEncoding = [Text.Encoding]::UTF8
                      {string.Join("\n", values.Select(_ => $"Write-Output {PowerShell.Quote(_)}"))}
                      """;
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(PowerShell.Encode(script));
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(output.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries))
            .IsEquivalentTo(values, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
