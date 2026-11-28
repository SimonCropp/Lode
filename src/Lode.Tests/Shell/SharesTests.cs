/// <summary>
/// What a machine on the network shares. WSL's is read from where WSL keeps its distributions,
/// a scratch key here, so these hold on any machine, with WSL or without.
/// </summary>
public class SharesTests
{
    // WSL's machine answers nobody asking what it shares, as net view \\wsl.localhost says
    // Access is denied, so its shares are the distributions WSL has installed, under the name
    // Windows gives it now and the one it had first. A key with no name is no distribution.
    [Test]
    public async Task WslSharesItsDistributions()
    {
        using var scratch = new RegistryScratch();
        var distributions = $@"{scratch.Root}\Lxss";
        Install(distributions, "{6bd6b5b1-7e2c-4a8a-a2ea-1e6f0e1c3a01}", "Ubuntu");
        Install(distributions, "{6bd6b5b1-7e2c-4a8a-a2ea-1e6f0e1c3a02}", "Debian");
        Registry.CurrentUser.CreateSubKey($@"{distributions}\{{6bd6b5b1-7e2c-4a8a-a2ea-1e6f0e1c3a03}}").Dispose();

        var localhost = Shares.List(@"\\wsl.localhost", distributions);
        var first = Shares.List(@"\\WSL$", distributions);

        await Assert.That(localhost).IsEquivalentTo([("Ubuntu", false), ("Debian", false)], CollectionOrdering.Matching);
        await Assert.That(first).IsEquivalentTo([("Ubuntu", false), ("Debian", false)], CollectionOrdering.Matching);
    }

    // Without WSL there is nothing to list, rather than a machine that can't be asked.
    [Test]
    public async Task WslWithoutDistributionsSharesNothing()
    {
        using var scratch = new RegistryScratch();

        var shares = Shares.List(@"\\wsl.localhost", $@"{scratch.Root}\Lxss");

        await Assert.That(shares).IsEmpty();
    }

    static void Install(string distributions, string key, string name)
    {
        using var distribution = Registry.CurrentUser.CreateSubKey($@"{distributions}\{key}");
        distribution.SetValue("DistributionName", name);
        distribution.SetValue("State", 1, RegistryValueKind.DWord);
    }
}
