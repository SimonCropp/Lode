/// <summary>
/// The Windows Search service as Options shows it, and the PowerShell that changes it, which only
/// ever runs from the Options buttons.
/// </summary>
public class WindowsSearchServiceTests
{
    [Test]
    public Task TurnOffScript() =>
        Verify(WindowsSearchService.TurnOffScript(@"C:\Temp\Bob's result.txt"))
            .Snapshot(
                """
                $ErrorActionPreference = 'Stop'
                $result = 'C:\Temp\Bob''s result.txt'
                try {
                    Set-Service -Name WSearch -StartupType Disabled
                    Stop-Service -Name WSearch -Force
                    Set-Content -LiteralPath $result -Value 'off' -Encoding UTF8
                    exit 0
                }
                catch {
                    Set-Content -LiteralPath $result -Value ('error: ' + $_.Exception.Message) -Encoding UTF8
                    exit 1
                }
                """);

    [Test]
    public Task TurnOnScript() =>
        Verify(WindowsSearchService.TurnOnScript(@"C:\Temp\Bob's result.txt"))
            .Snapshot(
                """
                $ErrorActionPreference = 'Stop'
                $result = 'C:\Temp\Bob''s result.txt'
                try {
                    $configured = & sc.exe config WSearch start= delayed-auto
                    if ($LASTEXITCODE -ne 0) {
                        throw ($configured -join ' ')
                    }
                    Start-Service -Name WSearch
                    Set-Content -LiteralPath $result -Value 'on' -Encoding UTF8
                    exit 0
                }
                catch {
                    Set-Content -LiteralPath $result -Value ('error: ' + $_.Exception.Message) -Encoding UTF8
                    exit 1
                }
                """);

    // As the service key's values say how it starts.
    [Test]
    public Task StartupOf() =>
        Verify(
            new Dictionary<string, WindowsSearchService.Startup>
            {
                ["2"] = WindowsSearchService.StartupOf(2, null),
                ["2, delayed"] = WindowsSearchService.StartupOf(2, 1),
                ["2, not delayed"] = WindowsSearchService.StartupOf(2, 0),
                ["3"] = WindowsSearchService.StartupOf(3, null),
                ["4"] = WindowsSearchService.StartupOf(4, 1),
                ["missing"] = WindowsSearchService.StartupOf(null, null),
                ["not a number"] = WindowsSearchService.StartupOf("2", null)
            })
            .Snapshot(
                """
                {
                  2: Automatic,
                  2, delayed: DelayedAutomatic,
                  2, not delayed: Automatic,
                  3: Manual,
                  4: Disabled,
                  missing: Missing,
                  not a number: Missing
                }
                """);

    [Test]
    public Task Describe() =>
        Verify(
            new WindowsSearchService.State[]
                {
                    new(WindowsSearchService.Startup.DelayedAutomatic, true),
                    new(WindowsSearchService.Startup.Automatic, false),
                    new(WindowsSearchService.Startup.Manual, true),
                    new(WindowsSearchService.Startup.Disabled, false),
                    new(WindowsSearchService.Startup.Disabled, true),
                    new(WindowsSearchService.Startup.Missing, false)
                }
                .Select(_ => $"{_.Startup}, running {_.Running}: {WindowsSearchService.Describe(_)} (on {_.IsOn}, off {_.IsOff})"))
                .Snapshot(
                    """
                    [
                      DelayedAutomatic, running True: Windows Search is running, and starts shortly after Windows does. (on True, off False),
                      Automatic, running False: Windows Search isn't running, and starts with Windows. (on False, off False),
                      Manual, running True: Windows Search is running, and starts when something asks for it. (on False, off False),
                      Disabled, running False: Windows Search is off. (on False, off True),
                      Disabled, running True: Windows Search is running, and is disabled. (on False, off False),
                      Missing, running False: Windows Search isn't installed. (on False, off False)
                    ]
                    """);
}
