/// <summary>
/// Excludes the code folder from Microsoft Defender's scanning, as Rider offers to: Defender
/// scanning every file a build or a git checkout writes slows both down. Changing Defender's
/// exclusions needs administrator rights, so this runs a PowerShell script behind Windows'
/// administrator prompt (<see cref="Elevated"/>). The script also removes the exclusions
/// already under the folder, which excluding the folder itself makes redundant, and reports
/// them.
/// </summary>
static class DefenderExclusion
{
    /// <param name="Cancelled">The administrator prompt was turned down.</param>
    /// <param name="Removed">The exclusions under the folder taken out as redundant.</param>
    /// <param name="Error">Why it failed, when it did and was not cancelled.</param>
    public sealed record Outcome(bool Succeeded, bool Cancelled, IReadOnlyList<string> Removed, string? Error);

    /// <summary>
    /// Excludes <paramref name="folder"/>, removing the exclusions under it.
    /// </summary>
    public static async Task<Outcome> Exclude(string folder)
    {
        var outcome = await Elevated.RunPowerShell(resultFile => Script(folder, resultFile));
        return new(outcome.Succeeded, outcome.Cancelled, outcome.Lines, outcome.Error);
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
