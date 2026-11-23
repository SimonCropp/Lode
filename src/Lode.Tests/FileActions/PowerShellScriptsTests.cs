public class PowerShellScriptsTests
{
    // Each script runs in a tab of its own, in its folder, left at a prompt after. The command
    // is shown decoded: a quote in a name is doubled, and nothing else in it is touched.
    [Test]
    public Task Tabs()
    {
        var tabs = PowerShellScripts.Tabs(
            [@"C:\Code\Lode\build.ps1", @"C:\Two Words\it's; done.ps1"],
            @"C:\Program Files\PowerShell\7\pwsh.exe");

        return Verify(
            tabs.Select(_ => new
            {
                _.Directory,
                Shell = _.Command[0],
                Arguments = _.Command.Skip(1).SkipLast(1),
                Command = Encoding.Unicode.GetString(Convert.FromBase64String(_.Command[^1]))
            }))
            .Snapshot(
                """
                [
                  {
                    Directory: C:\Code\Lode,
                    Shell: C:\Program Files\PowerShell\7\pwsh.exe,
                    Arguments: [
                      -NoExit,
                      -EncodedCommand
                    ],
                    Command:
                if ((Get-ExecutionPolicy) -ne 'AllSigned') { try { Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction Stop } catch { } }
                & 'C:\Code\Lode\build.ps1'
                  },
                  {
                    Directory: C:\Two Words,
                    Shell: C:\Program Files\PowerShell\7\pwsh.exe,
                    Arguments: [
                      -NoExit,
                      -EncodedCommand
                    ],
                    Command:
                if ((Get-ExecutionPolicy) -ne 'AllSigned') { try { Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction Stop } catch { } }
                & 'C:\Two Words\it''s; done.ps1'
                  }
                ]
                """);
    }

    [Test]
    public async Task AreScripts()
    {
        await Assert.That(PowerShellScripts.AreScripts([@"C:\a.ps1", @"C:\b.PS1"])).IsTrue();
        await Assert.That(PowerShellScripts.AreScripts([@"C:\a.ps1", @"C:\b.txt"])).IsFalse();
        await Assert.That(PowerShellScripts.AreScripts([@"C:\module.psm1"])).IsFalse();
    }
}
