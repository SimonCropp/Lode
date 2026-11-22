/// <summary>
/// The Windows Search service as Options shows it, and the PowerShell that changes it, which only
/// ever runs from the Options buttons.
/// </summary>
public class WindowsSearchServiceTests
{
    [Test]
    public Task TurnOffScript() =>
        Verify(WindowsSearchService.TurnOffScript(@"C:\Temp\Bob's result.txt"));

    [Test]
    public Task TurnOnScript() =>
        Verify(WindowsSearchService.TurnOnScript(@"C:\Temp\Bob's result.txt"));

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
            });

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
                .Select(_ => $"{_.Startup}, running {_.Running}: {WindowsSearchService.Describe(_)} (on {_.IsOn}, off {_.IsOff})"));
}
