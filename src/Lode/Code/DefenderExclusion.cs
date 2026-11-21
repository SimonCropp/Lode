/// <summary>
/// Excludes the code folder from Microsoft Defender's scanning, as Rider offers to: Defender
/// scanning every file a build or a git checkout writes slows both down. Changing Defender's
/// exclusions needs administrator rights, so this runs a PowerShell script behind Windows'
/// administrator prompt. The script also removes the exclusions already under the folder,
/// which excluding the folder itself makes redundant, and reports them.
/// </summary>
static class DefenderExclusion
{
    /// <param name="Cancelled">The administrator prompt was turned down.</param>
    /// <param name="Removed">The exclusions under the folder taken out as redundant.</param>
    /// <param name="Error">Why it failed, when it did and was not cancelled.</param>
    public sealed record Outcome(bool Succeeded, bool Cancelled, IReadOnlyList<string> Removed, string? Error);

    const int cancelledByUser = 1223;

    /// <summary>
    /// Excludes <paramref name="folder"/>, removing the exclusions under it.
    /// </summary>
    public static async Task<Outcome> Exclude(string folder)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"lode-defender-{Guid.NewGuid():N}.txt");
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
            startInfo.ArgumentList.Add(PowerShell.Encode(Script(folder, resultFile)));

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new(false, false, [], "PowerShell didn't start.");
            }

            await process.WaitForExitAsync();
            return Read(process.ExitCode, resultFile);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == cancelledByUser)
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

    // The script writes the exclusions it removed, one a line, or "error: " and why.
    static Outcome Read(int exitCode, string resultFile)
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

    /// <summary>
    /// The PowerShell that does it, run elevated. An exclusion is under the folder when it is
    /// below it, so "C:\Code\Lode" is under "C:\Code" but "C:\CodeOther" is not. The folder's
    /// own entry is left, and added when missing.
    /// </summary>
    internal static string Script(string folder, string resultFile) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          $folder = {{PowerShell.Quote(folder)}}
          $result = {{PowerShell.Quote(resultFile)}}
          try {
              $root = $folder.TrimEnd('\') + '\'
              $under = @((Get-MpPreference).ExclusionPath | Where-Object {
                  $_ -and
                  ($_.TrimEnd('\') + '\') -ne $root -and
                  ($_.TrimEnd('\') + '\').StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
              })
              if ($under.Count -gt 0) {
                  Remove-MpPreference -ExclusionPath $under
              }
              Add-MpPreference -ExclusionPath $folder
              Set-Content -LiteralPath $result -Value $under -Encoding UTF8
              exit 0
          }
          catch {
              Set-Content -LiteralPath $result -Value ('error: ' + $_.Exception.Message) -Encoding UTF8
              exit 1
          }
          """;
}
