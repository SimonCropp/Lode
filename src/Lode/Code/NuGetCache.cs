/// <summary>
/// Purge NuGet cache: clears NuGet's caches on this machine, as
/// <c>dotnet nuget locals all --clear</c> does. That is the packages restore has fetched, the
/// HTTP cache, and the temp and plugins caches. The next restore fetches what it needs again.
/// </summary>
static class NuGetCache
{
    /// <summary>
    /// Where restore puts the packages it fetches, as NuGet's settings have it, for the Actions
    /// panel to name: the biggest of the caches, and the one people know.
    /// </summary>
    public static string PackagesFolder()
    {
        try
        {
            return NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(NuGet.Configuration.Settings.LoadDefaultSettings(Path.GetTempPath()));
        }
        catch (Exception)
        {
            // NuGet's own default, for a config it can't read, which the purge then fails on.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }
    }

    /// <summary>
    /// Clears the caches, once no update is running, which would be restoring from them. It can't
    /// be stopped: stopped part way, a package's folder could be left with some of its files
    /// gone, which restore would still take as complete.
    /// </summary>
    public static async Task<ActionLog.Outcome> Purge(IProgress<string>? progress = null)
    {
        var gate = CodeCommands.NugetGate;
        if (!await gate.WaitAsync(0))
        {
            progress?.Report("Waiting for Update NuGets to finish");
            await gate.WaitAsync();
        }

        try
        {
            var startInfo = StartInfo();
            var (exitCode, output) = await Task.Run(() => Run(startInfo));
            return Outcome(exitCode, output);
        }
        catch (Exception exception)
        {
            return new(false, "Failed", exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Runs dotnet, giving the code it exited with and what it wrote. Tests replace it, so they
    /// leave the machine's caches alone.
    /// </summary>
    internal static Func<ProcessStartInfo, (int ExitCode, string Output)> Run = RunProcess;

    static (int ExitCode, string Output) RunProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, $"{output}\n{error.Result}".Trim());
    }

    /// <summary>
    /// The dotnet running Lode, rather than whichever is first on the PATH, which for a Lode
    /// started at login, or by Explorer, may have none. Run in the temp folder, as the folder Lode
    /// was started in could hold a NuGet.config that puts the caches somewhere else.
    /// </summary>
    internal static ProcessStartInfo StartInfo() =>
        new(ToolUpdate.RunningDotnet())
        {
            ArgumentList =
            {
                "nuget",
                "locals",
                "all",
                "--clear"
            },
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

    /// <summary>
    /// What dotnet said, as the Actions panel shows it: each cache it cleared, or what it
    /// couldn't delete, a package a build or Visual Studio has open say.
    /// </summary>
    internal static ActionLog.Outcome Outcome(int exitCode, string output)
    {
        if (exitCode == 0)
        {
            return new(true, "Cleared", output);
        }

        return new(false, "Failed", output);
    }
}
