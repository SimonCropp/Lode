using System.Text;

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
/// keeping only Windows: the macOS and Linux shells, and the folders besides the store to stop
/// processes under, are left out.
/// </para>
/// </summary>
/// <param name="packageId">The tool's package.</param>
/// <param name="shim">
/// The tool's command, which <c>dotnet tool install</c> puts in the tool path and which stays the
/// same across updates. <see cref="ShimFor"/> finds it from a path under the store.
/// </param>
/// <param name="outcome">Where the shell writes how the update went.</param>
sealed class ToolUpdate(string packageId, string shim, string outcome)
{
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

    public string PackageId => packageId;

    public string Shim => shim;

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
    public string StoreDirectory => Path.Combine(ToolPath, ".store", packageId);

    /// <summary>
    /// The dotnet that runs this process, rather than whichever is first on the PATH, which for a
    /// Lode started at login, or by Explorer, may have none.
    /// </summary>
    public string Dotnet { get; init; } = RunningDotnet();

    /// <summary>
    /// Starts the shell and returns. Exiting is the caller's, and has to be: the shell waits for
    /// this process to go before it updates.
    /// </summary>
    public void Start()
    {
        using var process = Process.Start(StartInfo(Environment.ProcessId));
    }

    public ProcessStartInfo StartInfo(int processId)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(processId)));
        return new("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Unrestricted",
                "-EncodedCommand",
                encoded
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            // A tool runs with its own folder under .store as the working directory, and the shell
            // would inherit it. Windows will not delete a folder that is a process's working
            // directory, so the uninstall the update starts with would be denied the very version
            // it is replacing, by the shell replacing it.
            WorkingDirectory = Path.GetTempPath()
        };
    }

    /// <summary>
    /// Waits for Lode to go, rather than for a guess at how long that takes, and silent where it
    /// already has, which is the usual case. Then stops whatever still holds the installed
    /// version, since the uninstall the update starts with deletes the whole version's folder and
    /// one open file under it fails all of it, as "Access to the path ... is denied": the shim, or
    /// anything else started out of the store.
    /// </summary>
    public string Script(int processId)
    {
        var quotedShim = Quote(shim);
        var store = Quote($@"{StoreDirectory.TrimEnd('\\')}\");
        string[] steps =
        [
            $"Wait-Process -Id {processId} -Timeout {waitSeconds} -ErrorAction SilentlyContinue",
            $$"""$locking = @(Get-Process | Where-Object { $_.Path -and ($_.Path -eq {{quotedShim}} -or $_.Path.StartsWith({{store}}, 'OrdinalIgnoreCase')) })""",
            "$locking | Stop-Process -Force -ErrorAction SilentlyContinue",
            $"$locking | Wait-Process -Timeout {stopSeconds} -ErrorAction SilentlyContinue",
            $$"""$output = & {{Quote(Dotnet)}} tool update {{packageId}} --tool-path {{Quote(ToolPath)}} --prerelease 2>&1 | ForEach-Object { "$_" }""",
            $$"""$status = if ($LASTEXITCODE -eq 0) { '{{ToolUpdateOutcome.SucceededLine}}' } else { '{{ToolUpdateOutcome.FailedLine}}' }""",
            $"Set-Content -LiteralPath {Quote(outcome)} -Value (@($status) + $output) -Encoding UTF8",
            $"& {quotedShim}"
        ];
        return string.Join("; ", steps);
    }

    static string Quote(string path) =>
        $"'{path.Replace("'", "''")}'";

    /// <summary>
    /// The shim of the tool whose store <paramref name="path"/> is under, or null for a path
    /// outside one, such as a build's output, with nothing to update. <c>dotnet tool install</c>
    /// puts a tool's files under <c>{tools}\.store\{package}\</c> and its command in
    /// <c>{tools}</c>, for <c>--global</c> and <c>--tool-path</c> alike.
    /// </summary>
    public static string? ShimFor(string path, string command)
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

    static string RunningDotnet()
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
