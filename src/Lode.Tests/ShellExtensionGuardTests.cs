/// <summary>
/// An extension that was being started when a menu timed out is refused from then on.
/// </summary>
[TUnit.Core.Executors.STAThreadExecutor]
public class ShellExtensionGuardTests
{
    [Test]
    public async Task RefusesWhatWasStartingWhenItTimedOut()
    {
        var stuck = Guid.NewGuid();
        var earlier = Guid.NewGuid();
        using (var guard = new ShellExtensionGuard())
        {
            guard.RecordActivation(earlier);
            guard.RecordActivation(stuck);
            guard.CancelCall();
        }

        var untouched = Guid.NewGuid();
        using (var guard = new ShellExtensionGuard())
        {
            // Nothing timed out.
            guard.RecordActivation(untouched);
        }

        await Verify(
            new
            {
                stuck = ShellExtensionGuard.IsRefused(stuck),
                earlier = ShellExtensionGuard.IsRefused(earlier),
                untouched = ShellExtensionGuard.IsRefused(untouched)
            });
    }
}
