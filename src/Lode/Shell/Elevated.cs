/// <summary>
/// Runs a PowerShell script of Lode's behind Windows' administrator prompt, for what only an
/// administrator may change: Defender's exclusions (<see cref="DefenderExclusion"/>), or the
/// Windows Search service (<see cref="WindowsSearchService"/>). What an elevated process writes
/// can't be read from it, so the script writes what it has to say to a file, one line at a time,
/// or "error: " and why, and exits 1 for that.
/// </summary>
static class Elevated
{
    /// <param name="Cancelled">The administrator prompt was turned down.</param>
    /// <param name="Lines">What the script wrote, when it succeeded.</param>
    /// <param name="Error">Why it failed, when it did and was not cancelled.</param>
    public sealed record Outcome(bool Succeeded, bool Cancelled, IReadOnlyList<string> Lines, string? Error);

    const int cancelledByUser = 1223;

    /// <param name="script">The script, given the file it is to write to.</param>
    public static async Task<Outcome> RunPowerShell(Func<string, string> script)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"lode-elevated-{Guid.NewGuid():N}.txt");
        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(PowerShell.Encode(script(resultFile)));

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new(false, false, [], "PowerShell didn't start.");
            }

            await process.WaitForExitAsync();
            return Read(process.ExitCode, resultFile);
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == cancelledByUser)
        {
            return new(false, true, [], null);
        }
        catch (Exception exception)
        {
            return new(false, false, [], exception.Message);
        }
        finally
        {
            File.Delete(resultFile);
        }
    }

    internal static Outcome Read(int exitCode, string resultFile)
    {
        string[] lines = [];
        if (File.Exists(resultFile))
        {
            lines = File.ReadAllLines(resultFile)
                .Where(_ => _.Length > 0)
                .ToArray();
        }

        const string errorPrefix = "error: ";
        if (exitCode != 0)
        {
            var error = lines.FirstOrDefault(_ => _.StartsWith(errorPrefix, StringComparison.Ordinal))?[errorPrefix.Length..];
            return new(false, false, [], error ?? $"PowerShell stopped with code {exitCode}.");
        }

        return new(true, false, lines, null);
    }
}
