/// <summary>
/// The Windows Search service, WSearch, which indexes files for Start's and Settings' search,
/// Outlook's and OneNote's, and Explorer's own. Lode never asks it anything: its search, by name
/// or of what files hold, reads the folders itself. So it can be turned off for good, which stops
/// its indexing, and on again as Windows has it, which takes administrator permission
/// (<see cref="Elevated"/>). Reading how it is takes none.
/// </summary>
static class WindowsSearchService
{
    const string serviceName = "WSearch";
    const string serviceKey = @"SYSTEM\CurrentControlSet\Services\WSearch";

    /// <summary>
    /// How the service starts, as its registry key says.
    /// </summary>
    public enum Startup
    {
        Missing,
        Automatic,
        DelayedAutomatic,
        Manual,
        Disabled
    }

    /// <param name="Running">Whether it is running now.</param>
    public sealed record State(Startup Startup, bool Running)
    {
        /// <summary>
        /// Off for good: it won't start, and isn't running.
        /// </summary>
        public bool IsOff => Startup == Startup.Disabled && !Running;

        /// <summary>
        /// As Windows has it: starting by itself, shortly after Windows does, and running.
        /// </summary>
        public bool IsOn => Startup is Startup.Automatic or Startup.DelayedAutomatic &&
                            Running;
    }

    /// <summary>
    /// How it is now.
    /// </summary>
    public static State Read() =>
        new(ReadStartup(), IsRunning());

    static Startup ReadStartup()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(serviceKey);
            if (key == null)
            {
                return Startup.Missing;
            }

            return StartupOf(key.GetValue("Start"), key.GetValue("DelayedAutostart"));
        }
        catch (Exception exception) when (exception is SecurityException or IOException or UnauthorizedAccessException)
        {
            return Startup.Missing;
        }
    }

    /// <summary>
    /// What the service key's Start and DelayedAutostart values say: 2 automatic, delayed when
    /// DelayedAutostart is 1, 3 manual, 4 disabled.
    /// </summary>
    internal static Startup StartupOf(object? start, object? delayed)
    {
        if (start is not int value)
        {
            return Startup.Missing;
        }

        switch (value)
        {
            case 2 when delayed is 1:
                return Startup.DelayedAutomatic;
            case 2:
                return Startup.Automatic;
            case 3:
                return Startup.Manual;
            case 4:
                return Startup.Disabled;
            default:
                return Startup.Missing;
        }
    }

    static bool IsRunning()
    {
        var manager = OpenSCManager(null, null, ServiceManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return QueryServiceStatus(service, out var status) &&
                       status.CurrentState == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// How it is, said in a sentence for Options.
    /// </summary>
    internal static string Describe(State state)
    {
        if (state.Startup == Startup.Missing)
        {
            return "Windows Search isn't installed.";
        }

        if (state.IsOff)
        {
            return "Windows Search is off.";
        }

        var startup = state.Startup switch
        {
            Startup.DelayedAutomatic => "starts shortly after Windows does",
            Startup.Automatic => "starts with Windows",
            Startup.Manual => "starts when something asks for it",
            _ => "is disabled"
        };
        if (state.Running)
        {
            return $"Windows Search is running, and {startup}.";
        }

        return $"Windows Search isn't running, and {startup}.";
    }

    /// <summary>
    /// Stops the service and disables it, so it doesn't start again.
    /// </summary>
    public static Task<Elevated.Outcome> TurnOff() =>
        Elevated.RunPowerShell(TurnOffScript);

    /// <summary>
    /// Starts the service, set to start by itself shortly after Windows does, as Windows has it.
    /// </summary>
    public static Task<Elevated.Outcome> TurnOn() =>
        Elevated.RunPowerShell(TurnOnScript);

    /// <summary>
    /// The PowerShell that turns it off, run elevated. Disabled first, so nothing starts it again
    /// between the stop and that.
    /// </summary>
    internal static string TurnOffScript(string resultFile) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          $result = {{PowerShell.Quote(resultFile)}}
          try {
              Set-Service -Name {{serviceName}} -StartupType Disabled
              Stop-Service -Name {{serviceName}} -Force
              Set-Content -LiteralPath $result -Value 'off' -Encoding UTF8
              exit 0
          }
          catch {
              Set-Content -LiteralPath $result -Value ('error: ' + $_.Exception.Message) -Encoding UTF8
              exit 1
          }
          """;

    /// <summary>
    /// The PowerShell that turns it on, run elevated. Windows PowerShell's Set-Service can't make
    /// a service's start delayed, so sc does that.
    /// </summary>
    internal static string TurnOnScript(string resultFile) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          $result = {{PowerShell.Quote(resultFile)}}
          try {
              $configured = & sc.exe config {{serviceName}} start= delayed-auto
              if ($LASTEXITCODE -ne 0) {
                  throw ($configured -join ' ')
              }
              Start-Service -Name {{serviceName}}
              Set-Content -LiteralPath $result -Value 'on' -Encoding UTF8
              exit 0
          }
          catch {
              Set-Content -LiteralPath $result -Value ('error: ' + $_.Exception.Message) -Encoding UTF8
              exit 1
          }
          """;
}
