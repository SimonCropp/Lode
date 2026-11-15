/// <summary>
/// The update Lode runs on itself, with stand-ins for dotnet and for the shim, so nothing is
/// installed or updated. Ported from PlatformHeads' tests, with ToolUpdate.
/// </summary>
public class ToolUpdateTests
{
    // A RID specific tool, as .NET 10 lays one out, and a portable one. A build's output is in no
    // store, and has no shim.
    [Test]
    public Task ShimFor() =>
        Verify(
            new
            {
                ridSpecific = ToolUpdate.ShimFor(@"C:\Users\Someone\.dotnet\tools\.store\lode\1.0.0\lode.win-x64\1.0.0\tools\net11.0\win-x64\Lode.dll"),
                portable = ToolUpdate.ShimFor(@"C:\Users\Someone\.dotnet\tools\.store\lode\1.0.0\lode\1.0.0\tools\net11.0\any\Lode.dll"),
                buildHasNone = ToolUpdate.ShimFor(@"C:\Code\Lode\src\Lode\bin\Release\net11.0\Lode.dll") == null
            });

    // Hidden, from anywhere but the store, whose folders the update deletes, and Windows will not
    // delete one that a running process is sitting in. The script goes encoded, so no quoting of
    // it reaches the command line.
    [Test]
    public Task PowerShellIsStarted()
    {
        var update = new ToolUpdate(@"C:\Users\Someone\.dotnet\tools\lode.exe", "outcome");

        var info = update.StartInfo(4242);

        return Verify(
            new
            {
                info.FileName,
                arguments = info.ArgumentList.SkipLast(1),
                scriptEncoded = Encoding.Unicode.GetString(Convert.FromBase64String(info.ArgumentList.Last())) == update.Script(4242),
                info.CreateNoWindow,
                info.UseShellExecute,
                inTemp = info.WorkingDirectory == Path.GetTempPath()
            });
    }

    // Under a user name with a quote in it, so the quoting is pinned as well, along with the tool
    // path the update is by and the store whose processes it stops.
    [Test]
    public Task Script()
    {
        var update = new ToolUpdate(@"C:\Users\O'Brien\.dotnet\tools\lode.exe", @"C:\Users\O'Brien\AppData\Roaming\Lode\update-outcome.log")
        {
            Dotnet = @"C:\Program Files\dotnet\dotnet.exe"
        };

        return Verify(update.Script(4242));
    }

    /// <summary>
    /// The shell, run: it waits for Lode, updates by the tool path, says how that went, and starts
    /// the shim whether it worked or not. Lode is a process of the test's, and dotnet and the shim
    /// are scripts that say what they were asked.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TheUpdateWaitsForLodeThenStartsTheShim(bool succeeds)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Lode.ToolUpdate.{Guid.NewGuid():N}");
        var tools = Path.Combine(directory, "tools");
        Directory.CreateDirectory(tools);
        var updated = Path.Combine(directory, "updated.txt");
        var started = Path.Combine(directory, "started.txt");
        var outcome = Path.Combine(directory, "outcome.log");
        var said = "It could not be updated.";
        if (succeeds)
        {
            said = "Tool 'lode' was successfully updated.";
        }

        var (dotnet, shim) = StandIns(directory, tools, updated, started, said, succeeds ? 0 : 1);
        using var lode = StandInForLode();
        try
        {
            var update = new ToolUpdate(shim, outcome)
            {
                Dotnet = dotnet
            };
            using (Process.Start(update.StartInfo(lode.Id)))
            {
            }

            // Only a statement about what has not happened yet, which a slow machine cannot make
            // false.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(File.Exists(updated)).IsFalse();

            lode.Kill();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(started) &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            await Assert.That(File.Exists(started)).IsTrue();
            var arguments = (await File.ReadAllTextAsync(updated)).Split((char[]) ['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(string.Join('\n', arguments)).IsEqualTo(string.Join('\n', "tool", "update", "Lode", "--tool-path", tools, "--prerelease"));
            var result = ToolUpdateOutcome.Take(outcome)!;
            await Assert.That(result.Succeeded).IsEqualTo(succeeds);
            await Assert.That(result.Reason).IsEqualTo(said);
        }
        finally
        {
            if (!lode.HasExited)
            {
                lode.Kill();
            }

            await Delete(directory);
        }
    }

    /// <summary>
    /// The folder deleted, and again a little later while Windows says something in it is in use,
    /// for up to five seconds: a scanner looking at a file the stand-ins just wrote can hold it as
    /// the test ends, and that is no reason to fail a test that passed.
    /// </summary>
    static async Task Delete(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (Exception exception)
                when ((exception is IOException or UnauthorizedAccessException) &&
                      attempt < 20)
            {
                await Task.Delay(250);
            }
        }
    }

    // A process that runs until the test ends it.
    static Process StandInForLode()
    {
        var info = new ProcessStartInfo("ping.exe", ["-n", "120", "127.0.0.1"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        return Process.Start(info)!;
    }

    /// <summary>
    /// dotnet, which writes the arguments it was given and then what it says, and exits with
    /// <paramref name="code"/>, and the shim, which says it was started. Each a .cmd, since the
    /// shell starts whatever it is named. Paths with no spaces, as a temp folder's are, so a .cmd
    /// can write them as they came.
    /// </summary>
    static (string Dotnet, string Shim) StandIns(string directory, string tools, string updated, string started, string said, int code)
    {
        var dotnet = Path.Combine(directory, "dotnet.cmd");
        File.WriteAllText(dotnet, $"@echo off\r\necho %*> \"{updated}\"\r\necho {said}\r\nexit /b {code}\r\n");

        // Written under another name and renamed, so started.txt is there only once cmd has closed
        // it: the test goes on the moment it appears, and Windows will not delete a file that is
        // still open.
        var shim = Path.Combine(tools, "lode.cmd");
        var writing = $"{started}.writing";
        File.WriteAllText(shim, $"@echo off\r\necho started> \"{writing}\"\r\nren \"{writing}\" \"{Path.GetFileName(started)}\"\r\n");
        return (dotnet, shim);
    }
}
