/// <summary>
/// The PowerShell that excludes the code folder, which only ever runs from the Options button.
/// </summary>
public class DefenderExclusionTests
{
    // A quote in a path is doubled, so it can't end the string early.
    [Test]
    public Task Script() =>
        Verify(DefenderExclusion.Script(@"D:\Bob's Code", @"C:\Temp\result.txt"))
            .Snapshot(
                """
                $ErrorActionPreference = 'Stop'
                $folder = 'D:\Bob''s Code'
                $result = 'C:\Temp\result.txt'
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
                """);
}
