using Meridian.Services;

namespace Tests.Services;

[TestFixture]
class ShellRegistrationTests
{
    [Test]
    public void IsRegisteredAsShell_ReturnsFalseByDefault() =>
        Assert.That(ShellRegistration.IsRegisteredAsShell(), Is.False);

    [Test]
    public void IsRunningAsShell_ReturnsFalseByDefault() =>
        Assert.That(ShellRegistration.IsRunningAsShell(), Is.False);

    [Test]
    public void IsElevated_ReturnsBoolean()
    {
        var result = ShellRegistration.IsElevated();
        Assert.That(result, Is.TypeOf<bool>());
    }
}
