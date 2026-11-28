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
            })
            .Snapshot(
                """
                {
                  ridSpecific: C:\Users\Someone\.dotnet\tools\lode.exe,
                  portable: C:\Users\Someone\.dotnet\tools\lode.exe,
                  buildHasNone: true
                }
                """);

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
            })
            .Snapshot(
                """
                {
                  FileName: powershell.exe,
                  arguments: [
                    -NoProfile,
                    -ExecutionPolicy,
                    Unrestricted,
                    -EncodedCommand
                  ],
                  scriptEncoded: true,
                  CreateNoWindow: true,
                  UseShellExecute: false,
                  inTemp: true
                }
                """);
    }

    // Under a user name with a quote in it, so the quoting is pinned as well, along with the tool
    // path the update is by, the signal named for it, and the store whose processes it stops.
    [Test]
    public Task Script()
    {
        var update = new ToolUpdate(@"C:\Users\O'Brien\.dotnet\tools\lode.exe", @"C:\Users\O'Brien\AppData\Roaming\Lode\update-outcome.log")
        {
            Dotnet = @"C:\Program Files\dotnet\dotnet.exe"
        };

        return Verify(update.Script(4242))
            .Snapshot("""$security = [System.Security.AccessControl.EventWaitHandleSecurity]::new(); $security.AddAccessRule([System.Security.AccessControl.EventWaitHandleAccessRule]::new([System.Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'Allow')); $created = $false; $updating = [System.Threading.EventWaitHandle]::new($false, 'ManualReset', 'Local\Lode-Update-5629507FE0AF8DA57BA92EA447136EA8', [ref]$created, $security); try { $started = [System.Threading.EventWaitHandle]::OpenExisting('Local\Lode-Update-5629507FE0AF8DA57BA92EA447136EA8-Started'); $null = $started.Set(); $started.Dispose() } catch { }; Wait-Process -Id 4242 -Timeout 30 -ErrorAction SilentlyContinue; function Stop-Locking { $locking = @(Get-Process | Where-Object { $_.Path -and ($_.Path -eq 'C:\Users\O''Brien\.dotnet\tools\lode.exe' -or $_.Path.StartsWith('C:\Users\O''Brien\.dotnet\tools\.store\Lode\', 'OrdinalIgnoreCase')) }); $locking | Stop-Process -Force -ErrorAction SilentlyContinue; $locking | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue }; Stop-Locking; $update = { & 'C:\Program Files\dotnet\dotnet.exe' tool update Lode --tool-path 'C:\Users\O''Brien\.dotnet\tools' --prerelease 2>&1 | ForEach-Object { "$_" } }; $output = & $update; if ($LASTEXITCODE -ne 0 -and ($output -match 'denied|being used by another process')) { Stop-Locking; $output = & $update }; $status = if ($LASTEXITCODE -eq 0) { 'ok' } else { 'failed' }; Set-Content -LiteralPath 'C:\Users\O''Brien\AppData\Roaming\Lode\update-outcome.log' -Value (@($status) + $output) -Encoding UTF8; $null = $updating.Set(); & 'C:\Users\O''Brien\.dotnet\tools\lode.exe'""");
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
    /// Lode starting the update: from then till the update is done, a launch is put off, while
    /// Lode closes, once it has gone, and while dotnet runs, but a switch, which reports, is not.
    /// Once done, just before Lode is started again, nothing is. The Lode is a process of the
    /// test's, and dotnet a script that waits for the test to say it is done.
    /// </summary>
    [Test]
    public async Task ALaunchIsPutOffFromTheUpdateStartingTillItIsDone()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Lode.ToolUpdate.{Guid.NewGuid():N}");
        var tools = Path.Combine(directory, "tools");
        Directory.CreateDirectory(tools);
        var updating = Path.Combine(directory, "updating.txt");
        var go = Path.Combine(directory, "go.txt");
        var started = Path.Combine(directory, "started.txt");
        var dotnet = Path.Combine(directory, "dotnet.cmd");
        await File.WriteAllTextAsync(
            dotnet,
            $"""
             @echo off
             echo %*> "{updating}"
             :wait
             if exist "{go}" goto done
             ping -n 2 127.0.0.1 >nul
             goto wait
             :done
             echo Tool 'lode' was successfully updated.
             exit /b 0

             """.ReplaceLineEndings("\r\n"));
        var shim = Path.Combine(tools, "lode.cmd");
        var writing = $"{started}.writing";
        await File.WriteAllTextAsync(shim, $"@echo off\r\necho started> \"{writing}\"\r\nren \"{writing}\" \"{Path.GetFileName(started)}\"\r\n");
        var update = new ToolUpdate(shim, Path.Combine(directory, "outcome.log"))
        {
            Dotnet = dotnet
        };
        var instance = $"Lode-Tests-{Guid.NewGuid():N}";
        using var lode = StandInForLode();
        try
        {
            var before = update.IsRunning();
            var launchBefore = PutOff([directory]);

            bool whileLodeCloses;
            using (update.Start(lode.Id))
            {
                whileLodeCloses = update.IsRunning();
            }

            // Lode's hold on the signal gone, as it is once Lode exits: the shell's keeps it.
            var onceLodeHasGone = update.IsRunning();
            lode.Kill();
            await WaitFor(updating);
            var whileDotnetRuns = update.IsRunning();
            var switchWhileDotnetRuns = PutOff(["--help"]);
            await File.WriteAllTextAsync(go, "");
            await WaitFor(started);

            await Assert.That(before).IsFalse();
            await Assert.That(launchBefore).IsFalse();
            await Assert.That(whileLodeCloses).IsTrue();
            await Assert.That(onceLodeHasGone).IsTrue();
            await Assert.That(whileDotnetRuns).IsTrue();
            await Assert.That(switchWhileDotnetRuns).IsFalse();
            await Assert.That(update.IsRunning()).IsFalse();
            await Assert.That(PutOff([directory])).IsFalse();
        }
        finally
        {
            if (!lode.HasExited)
            {
                lode.Kill();
            }

            await Delete(directory);
        }

        // Put off is a shell started, which the next test runs; none is here.
        bool PutOff(string[] arguments) =>
            Program.PutOffForUpdate(CommandLine.Parse(arguments), arguments, update, instance);
    }

    /// <summary>
    /// A launch put off while the update runs: started again, as it was, once the update is done
    /// and the Lode it starts is running, to hand its folders to, and not before either. A folder
    /// with a space and a backslash at its end, which Windows PowerShell would split, and a link
    /// with an ampersand, reach the shim as the command line they were. The update is the test
    /// holding its signal, the Lode it starts the test taking the running Lode's mutex, and the
    /// shim a script that writes what it was given.
    /// </summary>
    [Test]
    public async Task ALaunchPutOffStartsOnceTheUpdateIsDoneAndLodeIsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Lode.ToolUpdate.{Guid.NewGuid():N}");
        var tools = Path.Combine(directory, "tools");
        Directory.CreateDirectory(tools);
        var started = Path.Combine(directory, "started.txt");
        var shim = Path.Combine(tools, "lode.cmd");
        var writing = $"{started}.writing";
        await File.WriteAllTextAsync(shim, $"@echo off\r\necho %*> \"{writing}\"\r\nren \"{writing}\" \"{Path.GetFileName(started)}\"\r\n");
        var update = new ToolUpdate(shim, Path.Combine(directory, "outcome.log"));
        var instance = $"Lode-Tests-{Guid.NewGuid():N}";
        string[] arguments = [@"C:\Two Words\", "x-github-client://openRepo/https://github.com/owner/name?branch=main&filepath=readme.md"];
        try
        {
            using var updating = new EventWaitHandle(false, EventResetMode.ManualReset, update.SignalName);
            await Assert.That(Program.PutOffForUpdate(CommandLine.Parse(arguments), arguments, update, instance)).IsTrue();

            // Only statements about what has not happened yet, which a slow machine cannot make
            // false.
            await Task.Delay(TimeSpan.FromSeconds(1));
            var whileUpdating = File.Exists(started);
            updating.Set();
            await Task.Delay(TimeSpan.FromSeconds(1));
            var beforeLodeIsBack = File.Exists(started);
            using var running = new Mutex(false, $@"Local\{instance}");
            await WaitFor(started);

            await Assert.That(whileUpdating).IsFalse();
            await Assert.That(beforeLodeIsBack).IsFalse();
            await Assert.That((await File.ReadAllTextAsync(started)).Trim())
                .IsEqualTo("""
                           "C:\Two Words\\" "x-github-client://openRepo/https://github.com/owner/name?branch=main&filepath=readme.md"
                           """);
        }
        finally
        {
            await Delete(directory);
        }
    }

    /// <summary>
    /// Something started from the store while the update runs, after the shell stopped what held
    /// it, a launch of an older Lode say, which fails the update with access denied: the shell
    /// stops it, and tries once more. Any other failure is said as it is, not tried again, and
    /// stops nothing. dotnet is a script that, the first time, waits for the test to start a
    /// process out of the store, a copy of ping.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnUpdateDeniedItsFilesStopsWhatHoldsThemAndTriesAgain(bool denied)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Lode.ToolUpdate.{Guid.NewGuid():N}");
        var tools = Path.Combine(directory, "tools");
        var store = Path.Combine(tools, ".store", "Lode");
        Directory.CreateDirectory(store);
        var first = Path.Combine(directory, "first.txt");
        var second = Path.Combine(directory, "second.txt");
        var go = Path.Combine(directory, "go.txt");
        var started = Path.Combine(directory, "started.txt");
        var outcome = Path.Combine(directory, "outcome.log");
        var said = "Version 9.9.9 of package lode is not found in NuGet feeds.";
        if (denied)
        {
            said = $@"Access to the path '{store}\Lode.dll' is denied.";
        }

        var dotnet = Path.Combine(directory, "dotnet.cmd");
        await File.WriteAllTextAsync(
            dotnet,
            $"""
             @echo off
             if exist "{first}" goto again
             echo %*> "{first}"
             :wait
             if exist "{go}" goto failed
             ping -n 2 127.0.0.1 >nul
             goto wait
             :failed
             echo {said}
             exit /b 1
             :again
             echo %*> "{second}"
             echo Tool 'lode' was successfully updated.
             exit /b 0

             """.ReplaceLineEndings("\r\n"));
        var shim = Path.Combine(tools, "lode.cmd");
        var writing = $"{started}.writing";
        await File.WriteAllTextAsync(shim, $"@echo off\r\necho started> \"{writing}\"\r\nren \"{writing}\" \"{Path.GetFileName(started)}\"\r\n");
        var ping = Path.Combine(store, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ping);
        using var lode = StandInForLode();
        Process? locking = null;
        try
        {
            var update = new ToolUpdate(shim, outcome)
            {
                Dotnet = dotnet
            };
            using (Process.Start(update.StartInfo(lode.Id)))
            {
            }

            lode.Kill();
            await WaitFor(first);
            locking = Process.Start(
                new ProcessStartInfo(ping, ["-n", "120", "127.0.0.1"])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                })!;
            await File.WriteAllTextAsync(go, "");
            await WaitFor(started);

            var result = ToolUpdateOutcome.Take(outcome)!;
            await Assert.That(File.Exists(second)).IsEqualTo(denied);
            await Assert.That(locking.WaitForExit(TimeSpan.FromSeconds(denied ? 10 : 1))).IsEqualTo(denied);
            await Assert.That(result.Succeeded).IsEqualTo(denied);
            if (!denied)
            {
                await Assert.That(result.Reason).IsEqualTo(said);
            }
        }
        finally
        {
            if (!lode.HasExited)
            {
                lode.Kill();
            }

            if (locking is { HasExited: false })
            {
                locking.Kill();
                await locking.WaitForExitAsync();
            }

            locking?.Dispose();
            await Delete(directory);
        }
    }

    // Up to a minute, for a shell on a busy machine.
    static async Task WaitFor(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(path) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await Assert.That(File.Exists(path)).IsTrue();
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
                when (exception is IOException or UnauthorizedAccessException &&
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
