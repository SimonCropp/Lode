/// <summary>
/// Purge NuGet cache. Dotnet is never run for real here, which would clear this machine's
/// caches: it is replaced with what it would say. The window tests replace it too, so these
/// take turns with them.
/// </summary>
[NotInParallel("windows")]
public class NuGetCacheTests
{
    // As the script it stands in for: dotnet nuget locals all --clear, the dotnet running Lode,
    // in the temp folder.
    [Test]
    public async Task RunsDotnetNugetLocalsAllClear()
    {
        var startInfo = NuGetCache.StartInfo();

        await Assert.That(startInfo.FileName).IsEqualTo(ToolUpdate.RunningDotnet());
        await Assert.That(Path.GetFileName(startInfo.FileName)).IsEqualTo("dotnet.exe");
        await Assert.That(startInfo.ArgumentList).IsEquivalentTo(["nuget", "locals", "all", "--clear"], CollectionOrdering.Matching);
        await Assert.That(startInfo.WorkingDirectory).IsEqualTo(Path.GetTempPath());
        await Assert.That(startInfo.CreateNoWindow).IsTrue();
    }

    // What dotnet said is the details: each cache cleared, or what it couldn't delete. A dotnet
    // that won't start fails too.
    [Test]
    public async Task Outcomes()
    {
        var run = NuGetCache.Run;
        try
        {
            NuGetCache.Run = _ => (0, "Clearing NuGet global packages folder: C:\\packages\nLocal resources cleared.");
            var cleared = await NuGetCache.Purge();
            NuGetCache.Run = _ => (1, "Failed to delete 'C:\\packages\\verify\\lib.dll'.\nClearing local resources failed: Unable to delete one or more files.");
            var failed = await NuGetCache.Purge();
            NuGetCache.Run = _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified.");
            var missing = await NuGetCache.Purge();

            await Assert.That(cleared).IsEqualTo(new(true, "Cleared", "Clearing NuGet global packages folder: C:\\packages\nLocal resources cleared."));
            await Assert.That(failed).IsEqualTo(new(false, "Failed", "Failed to delete 'C:\\packages\\verify\\lib.dll'.\nClearing local resources failed: Unable to delete one or more files."));
            await Assert.That(missing).IsEqualTo(new(false, "Failed", "The system cannot find the file specified."));
        }
        finally
        {
            NuGetCache.Run = run;
        }
    }

    // An update restores packages from the cache, so the purge waits for it to finish, saying
    // so, and runs dotnet only then.
    [Test]
    public async Task WaitsForAnUpdate()
    {
        var run = NuGetCache.Run;
        var gate = CodeCommands.NugetGate;
        var ran = false;
        var reports = new Reports();
        NuGetCache.Run = _ =>
        {
            ran = true;
            return (0, "Local resources cleared.");
        };
        try
        {
            Task<ActionLog.Outcome> purging;
            await gate.WaitAsync();
            try
            {
                purging = NuGetCache.Purge(reports);
                await Task.Delay(100);

                await Assert.That(ran).IsFalse();
                await Assert.That(reports.Lines).IsEquivalentTo(["Waiting for Update NuGets to finish"]);
            }
            finally
            {
                gate.Release();
            }

            var outcome = await purging;

            await Assert.That(ran).IsTrue();
            await Assert.That(outcome.Summary).IsEqualTo("Cleared");
        }
        finally
        {
            NuGetCache.Run = run;
        }
    }

    // Kept as they are made, on the thread that made them.
    sealed class Reports : IProgress<string>
    {
        public List<string> Lines { get; } = [];

        public void Report(string value) =>
            Lines.Add(value);
    }
}
