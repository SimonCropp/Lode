/// <summary>
/// An extension that was being started when a menu's call into it was cancelled is refused
/// from then on. A menu merely slow to build refuses nothing.
/// </summary>
[WindowThread]
// Swaps how a call is cancelled, which a menu built meanwhile by another test would use.
[NotInParallel("windows")]
public class ShellExtensionGuardTests
{
    [Test]
    public async Task RefusesWhatWasStartingWhenACallWasCancelled()
    {
        var stuck = Guid.NewGuid();
        var earlier = Guid.NewGuid();
        var cancelPendingCall = ShellExtensionGuard.CancelPendingCall;
        ShellExtensionGuard.CancelPendingCall = _ => true;
        try
        {
            using var guard = new ShellExtensionGuard();
            guard.RecordActivation(earlier);
            guard.RecordActivation(stuck);
            guard.CancelCall();
        }
        finally
        {
            ShellExtensionGuard.CancelPendingCall = cancelPendingCall;
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
            })
            .Snapshot(
                """
                {
                  stuck: true,
                  earlier: false,
                  untouched: false
                }
                """);
    }

    // Past the timeout with no call waiting, a first menu slow to build, say, or one held up
    // by an extension in Lode's own process: there is nothing to cancel, so nothing is refused.
    [Test]
    public async Task RefusesNothingWithNoCallToCancel()
    {
        var slow = Guid.NewGuid();
        using (var guard = new ShellExtensionGuard())
        {
            guard.RecordActivation(slow);
            guard.CancelCall();
        }

        await Assert.That(ShellExtensionGuard.IsRefused(slow)).IsFalse();
    }

    // The timeout runs from what was last started, not from when the menu began: one starting
    // extension after extension is slow, not stuck.
    [Test]
    public async Task OverdueOnlyOnceNothingHasStartedForTheTimeout()
    {
        var timeout = (long)ShellExtensionGuard.Timeout.TotalMilliseconds;
        var made = Environment.TickCount64;
        using var guard = new ShellExtensionGuard();
        var timedFromMaking = guard.Overdue(made + timeout + 50);

        await Task.Delay(200);
        guard.RecordActivation(Guid.NewGuid());
        var started = Environment.TickCount64;

        await Assert.That(timedFromMaking).IsTrue();
        await Assert.That(guard.Overdue(made + timeout + 50)).IsFalse();
        await Assert.That(guard.Overdue(started + timeout)).IsTrue();
    }
}
