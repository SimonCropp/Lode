using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
/// Updates Lode, installed as a dotnet tool, and starts it again. The update has to run once Lode
/// has exited, since a running executable cannot be replaced, and since the Lode it then starts
/// takes the single instance lock. So a hidden PowerShell is started that waits for Lode to go,
/// runs <c>dotnet tool update</c> and starts the shim, and Lode exits once <see cref="Start"/>
/// returns.
/// <para>
/// Nobody sees that shell, so how it went is written to <paramref name="outcome"/> for the Lode it
/// starts to say (<see cref="ToolUpdateOutcome"/>). The shim is started again whether the update
/// worked or not: a failed update should leave the old version running, not no Lode at all.
/// </para>
/// <para>
/// Vendored from PlatformHeads.Core's ToolUpdate, which DiffEngineTray and BuildMonitor update by,
/// keeping only Windows and only Lode: the macOS and Linux shells, and the folders besides the
/// store to stop processes under, are left out, and the package and command are Lode's.
/// </para>
/// </summary>
/// <param name="shim">
/// Lode's command, which <c>dotnet tool install</c> puts in the tool path and which stays the same
/// across updates. <see cref="ShimFor"/> finds it from a path under the store.
/// </param>
/// <param name="outcome">Where the shell writes how the update went.</param>
sealed class ToolUpdate(string shim, string outcome)
{
    /// <summary>
    /// The package Lode is published as, on NuGet and in the store.
    /// </summary>
    public const string PackageId = "Lode";

    /// <summary>
    /// The command the package installs, whose shim is <c>lode.exe</c>.
    /// </summary>
    const string command = "lode";

    /// <summary>
    /// How long the shell waits for Lode to exit before updating anyway. Long enough for a
    /// shutdown that waits on something in flight, short enough that a Lode which is never going
    /// to exit does not leave the user with no update and no reason for it.
    /// </summary>
    const int waitSeconds = 30;

    /// <summary>
    /// How long the shell waits for the processes it stopped to go, since the files they hold are
    /// only released once they have.
    /// </summary>
    const int stopSeconds = 5;

    /// <summary>
    /// How long <see cref="Start"/> waits for the shell to have the update's signal open, which
    /// is PowerShell starting up: well under this unless the machine is struggling.
    /// </summary>
    static TimeSpan startedWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a launch put off by the update waits for it before starting anyway: longer than
    /// any update takes, waiting for Lode, stopping what holds the store and trying twice, but
    /// not for ever should the shell have died without saying it was done.
    /// </summary>
    const int deferredMinutes = 5;

    /// <summary>
    /// How long a launch put off by the update then waits for the Lode the update starts, which
    /// it hands its folders to.
    /// </summary>
    const int restartSeconds = 30;

    public string Outcome => outcome;

    /// <summary>
    /// The folder the shim is in, which the update is told with <c>--tool-path</c>. That is the
    /// one argument whichever way the tool was installed: the folder of a global install is a tool
    /// path too, and <c>--tool-path</c> with it updates the global install as <c>--global</c>
    /// does.
    /// </summary>
    public string ToolPath => Path.GetDirectoryName(shim) ?? "";

    /// <summary>
    /// Where the store keeps every installed version of the package, which the update empties.
    /// Matched without regard to case by the script, since the store names the folder for the id
    /// in lower case.
    /// </summary>
    public string StoreDirectory => Path.Combine(ToolPath, ".store", PackageId);

    /// <summary>
    /// The dotnet that runs this process, rather than whichever is first on the PATH, which for a
    /// Lode started at login, or by Explorer, may have none.
    /// </summary>
    public string Dotnet { get; init; } = RunningDotnet();

    /// <summary>
    /// The update's signal, while this Lode exits: kept, unset, till the process ends.
    /// </summary>
    static EventWaitHandle? exiting;

    /// <summary>
    /// Starts the shell and returns. Exiting is the caller's, and has to be: the shell waits for
    /// this process to go before it updates.
    /// </summary>
    public void Start() =>
        exiting = Start(Environment.ProcessId);

    /// <summary>
    /// Starts the shell that updates once process <paramref name="processId"/> has gone, and
    /// returns once the shell has the update's signal open too, or plainly can't. The signal,
    /// unset, is returned for that process to keep till it ends, so every launch from now till
    /// the update is done is put off (see <see cref="Defer"/>), those made while this Lode closes
    /// too, which would otherwise hand their folders to a Lode about to go. The wait is so the
    /// signal lasts: with Lode gone before the shell had it open, it would go with the last
    /// handle on it, and a launch between would run from the store as before. Should the shell
    /// take longer than <see cref="startedWait"/>, that is what happens.
    /// </summary>
    internal EventWaitHandle Start(int processId)
    {
        var updating = Signal(SignalName);
        try
        {
            updating.Reset();
            using var started = Signal($"{SignalName}-Started");
            started.Reset();
            using var process = Process.Start(StartInfo(processId))!;
            var waited = Stopwatch.StartNew();
            while (!started.WaitOne(100) &&
                   !process.HasExited &&
                   waited.Elapsed < startedWait)
            {
            }
        }
        catch
        {
            updating.Dispose();
            throw;
        }

        return updating;
    }

    /// <summary>
    /// The name of the signal, unset, for as long as an update of the tool in
    /// <see cref="ToolPath"/> runs: from Lode starting the shell till the update is done and the
    /// shell about to start Lode again. An event rather than a mutex, which a launch could only
    /// ask after by taking it, and so, for that moment, look to the Lode the update starts like
    /// the update still running. In the session's namespace, which every copy of Lode in it is
    /// in, the copy run as administrator too, and named for the tool path, so a Debug build, in
    /// none, never waits on an installed Lode's update, nor one install on another's.
    /// </summary>
    internal string SignalName
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(ToolPath).ToUpperInvariant()));
            return $@"Local\Lode-Update-{Convert.ToHexString(hash)[..32]}";
        }
    }

    // Whichever copy makes it, Lode or the shell, lets the user in, as by default one made by the
    // copy run as administrator would let in only administrators, and so not the other copy.
    static EventWaitHandle Signal(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new(identity.User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out _, security);
    }

    /// <summary>
    /// Whether an update of this tool is running now. Asked by every launch, so cheap: one
    /// lookup of a name. One the signal can't be opened for is taken as none, rather than put
    /// off for nothing.
    /// </summary>
    public bool IsRunning()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(SignalName, out var updating))
            {
                return false;
            }

            using (updating)
            {
                return !updating.WaitOne(0);
            }
        }
        catch (Exception exception)
            when (
                exception is
                    UnauthorizedAccessException or
                    IOException or
                    WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts off a launch made while the update runs, Win+E say, or a folder another program
    /// opens with Lode in Explorer's place: a hidden shell starts it again, with the same
    /// arguments in the same folder, once the update is done and the Lode it starts is running,
    /// so the folders go to that Lode as any later launch's do. Waiting here instead would not
    /// do: this process runs from the very store the update replaces, and for as long as it
    /// runs, holds it, so the update would fail with access denied, or this launch be stopped
    /// part way. Throws what starting PowerShell throws.
    /// </summary>
    /// <param name="instance">The name of the mutex the running Lode holds
    /// (<see cref="SingleInstance"/>), there once the Lode the update starts is.</param>
    public void Defer(IReadOnlyList<string> arguments, string directory, string instance)
    {
        using var process = Process.Start(DeferredStartInfo(arguments, directory, instance));
    }

    internal ProcessStartInfo DeferredStartInfo(IReadOnlyList<string> arguments, string directory, string instance)
    {
        // The shell is in no folder of the store's, which the update could then not delete.
        if (Locations.IsWithin(directory, StoreDirectory))
        {
            directory = Path.GetTempPath();
        }

        return new("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Unrestricted",
                "-EncodedCommand",
                PowerShell.Encode(DeferredScript(arguments, directory, instance))
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
    }

    /// <summary>
    /// Waits for the update to be done, then for the Lode it starts to be running, for so long
    /// at most, and starts the launch again. The arguments go as the one command line they make,
    /// each quoted as the launch split them, as Windows PowerShell passing them one by one
    /// splits a folder with a space and a backslash at its end in two.
    /// </summary>
    internal string DeferredScript(IReadOnlyList<string> arguments, string directory, string instance)
    {
        var commandLine = string.Join(' ', arguments.Select(RepositoryLink.Quoted));
        string[] steps =
        [
            "$updating = $null",
            $"if ([System.Threading.EventWaitHandle]::TryOpenExisting({PowerShell.Quote(SignalName)}, [ref]$updating)) {{ $null = $updating.WaitOne([TimeSpan]::FromMinutes({deferredMinutes})); $updating.Dispose() }}",
            $"$deadline = [DateTime]::UtcNow.AddSeconds({restartSeconds})",
            $$"""function Test-Running { try { $running = $null; if ([System.Threading.Mutex]::TryOpenExisting({{PowerShell.Quote($@"Local\{instance}")}}, [ref]$running)) { $running.Dispose(); return $true }; return $false } catch { return $true } }""",
            "while (-not (Test-Running) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }",
            $"$launch = [System.Diagnostics.ProcessStartInfo]::new({PowerShell.Quote(shim)}, {PowerShell.Quote(commandLine)})",
            "$launch.UseShellExecute = $false",
            $"$launch.WorkingDirectory = {PowerShell.Quote(directory)}",
            "$null = [System.Diagnostics.Process]::Start($launch)"
        ];
        return string.Join("; ", steps);
    }

    public ProcessStartInfo StartInfo(int processId) =>
        new("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Unrestricted",
                "-EncodedCommand",
                PowerShell.Encode(Script(processId))
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            // A tool runs with its own folder under .store as the working directory, and the shell
            // would inherit it. Windows will not delete a folder that is a process's working
            // directory, so the uninstall the update starts with would be denied the very version
            // it is replacing, by the shell replacing it.
            WorkingDirectory = Path.GetTempPath()
        };

    /// <summary>
    /// Opens the update's signal, unset, which Lode made, or makes it should Lode have gone
    /// already, and tells Lode it has it, so it lasts past Lode, and sets it only once the update
    /// is done, just before starting Lode again: every launch till then is put off (see
    /// <see cref="Defer"/>). Waits for Lode to go, rather than for a guess at how long that
    /// takes, and silent where it already has, which is the usual case. Then stops whatever still
    /// holds the installed version, since the uninstall the update starts with deletes the whole
    /// version's folder and one open file under it fails all of it, as "Access to the path ...
    /// is denied": the shim, or anything else started out of the store. Something can still
    /// start from there meanwhile, a launch for as long as it takes to put itself off, or one of
    /// a Lode from before launches were, so an update denied its files, or told one is in use,
    /// stops whatever holds them again and tries once more, where it would otherwise leave the
    /// old version half uninstalled, or the new one not installed.
    /// </summary>
    public string Script(int processId)
    {
        var quotedShim = PowerShell.Quote(shim);
        var store = PowerShell.Quote($@"{StoreDirectory.TrimEnd('\\')}\");
        string[] steps =
        [
            "$security = [System.Security.AccessControl.EventWaitHandleSecurity]::new()",
            "$security.AddAccessRule([System.Security.AccessControl.EventWaitHandleAccessRule]::new([System.Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'Allow'))",
            "$created = $false",
            $"$updating = [System.Threading.EventWaitHandle]::new($false, 'ManualReset', {PowerShell.Quote(SignalName)}, [ref]$created, $security)",
            $"try {{ $started = [System.Threading.EventWaitHandle]::OpenExisting({PowerShell.Quote($"{SignalName}-Started")}); $null = $started.Set(); $started.Dispose() }} catch {{ }}",
            $"Wait-Process -Id {processId} -Timeout {waitSeconds} -ErrorAction SilentlyContinue",
            $$"""function Stop-Locking { $locking = @(Get-Process | Where-Object { $_.Path -and ($_.Path -eq {{quotedShim}} -or $_.Path.StartsWith({{store}}, 'OrdinalIgnoreCase')) }); $locking | Stop-Process -Force -ErrorAction SilentlyContinue; $locking | Wait-Process -Timeout {{stopSeconds}} -ErrorAction SilentlyContinue }""",
            "Stop-Locking",
            $$"""$update = { & {{PowerShell.Quote(Dotnet)}} tool update {{PackageId}} --tool-path {{PowerShell.Quote(ToolPath)}} --prerelease 2>&1 | ForEach-Object { "$_" } }""",
            "$output = & $update",
            "if ($LASTEXITCODE -ne 0 -and ($output -match 'denied|being used by another process')) { Stop-Locking; $output = & $update }",
            $$"""$status = if ($LASTEXITCODE -eq 0) { '{{ToolUpdateOutcome.SucceededLine}}' } else { '{{ToolUpdateOutcome.FailedLine}}' }""",
            $"Set-Content -LiteralPath {PowerShell.Quote(outcome)} -Value (@($status) + $output) -Encoding UTF8",
            "$null = $updating.Set()",
            $"& {quotedShim}"
        ];
        return string.Join("; ", steps);
    }

    /// <summary>
    /// Lode's shim, in the tool path whose store <paramref name="path"/> is under, or null for a
    /// path outside one, such as a build's output, with nothing to update.
    /// <c>dotnet tool install</c> puts a tool's files under <c>{tools}\.store\{package}\</c> and
    /// its command in <c>{tools}</c>, for <c>--global</c> and <c>--tool-path</c> alike.
    /// </summary>
    public static string? ShimFor(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var store = Array.IndexOf(segments, ".store");
        if (store <= 0)
        {
            return null;
        }

        var tools = string.Join(Path.DirectorySeparatorChar, segments[..store]);
        return Path.Combine(tools, $"{command}.exe");
    }

    internal static string RunningDotnet()
    {
        // The runtime is {root}\shared\Microsoft.NETCore.App\{version}\, and the muxer is in {root}.
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var muxer = Path.Combine(root, "dotnet.exe");
        if (File.Exists(muxer))
        {
            return muxer;
        }

        return "dotnet";
    }
}
