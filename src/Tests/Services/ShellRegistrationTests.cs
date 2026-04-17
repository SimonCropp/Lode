[TestFixture]
class ShellRegistrationTests
{
    const string winlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string shellValue = "Shell";

    [Test]
    public void IsRegisteredAsShell_ReturnsFalseByDefault() =>
        Assert.That(ShellRegistration.IsRegisteredAsShell(), Is.False);

    [Test]
    public void IsElevated_ReturnsBoolean()
    {
        var result = ShellRegistration.IsElevated();
        Assert.That(result, Is.TypeOf<bool>());
    }

    [Test]
    public void RegisterAsShell_PerUser_SetsCurrentUserRegistry()
    {
        try
        {
            ShellRegistration.RegisterAsShell(perUser: true);

            using var key = Registry.CurrentUser.OpenSubKey(winlogonKey);
            var value = key?.GetValue(shellValue) as string;
            Assert.That(value, Is.EqualTo(Environment.ProcessPath));
            Assert.That(ShellRegistration.IsRegisteredAsShell(), Is.True);
        }
        finally
        {
            ShellRegistration.UnregisterAsShell(perUser: true);
        }
    }

    [Test]
    public void UnregisterAsShell_PerUser_RemovesShellValue()
    {
        ShellRegistration.RegisterAsShell(perUser: true);
        ShellRegistration.UnregisterAsShell(perUser: true);

        using var key = Registry.CurrentUser.OpenSubKey(winlogonKey);
        var value = key?.GetValue(shellValue);
        Assert.That(value, Is.Null);
        Assert.That(ShellRegistration.IsRegisteredAsShell(), Is.False);
    }

    [Test]
    public void UnregisterAsShell_PerUser_DoesNotThrowWhenNotRegistered() =>
        Assert.DoesNotThrow(() => ShellRegistration.UnregisterAsShell(perUser: true));
}
