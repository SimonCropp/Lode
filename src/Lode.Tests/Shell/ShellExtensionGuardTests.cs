/// <summary>
/// An extension that was being started when a menu's call into it was cancelled is refused
/// from then on. A menu merely slow to build refuses nothing. An extension stuck once the menu
/// is built, asked for a verb, filling in a submenu or running a command, is left behind too.
/// </summary>
[WindowThread]
// Swaps how a call is cancelled, and how long the guard waits, which a menu built meanwhile by
// another test would use.
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

    // An extension that adds its entries, then sticks when asked for their verbs, as Lode asks
    // of every entry to leave some off, holds the menu, and every window with it, no longer than
    // one stuck adding them: the call is cancelled, and the menu shows, the entry kept, as
    // without a verb it is not known to be one to leave off. The extension is a stand-in, on a
    // thread of its own, called through COM, as one in another process is.
    [Test]
    public async Task AnExtensionStuckGivingAVerbIsLeftBehind()
    {
        using var timeouts = new ShortTimeouts();
        using var standIn = StandInMenu.StuckIn(StandInMenu.Call.GetCommandString, "stuck");
        using var menu = new ShellContextMenu();
        var shown = 0;
        menu.Track = (built, _) =>
        {
            shown = GetMenuItemCount(built);
            return 0;
        };

        var stopwatch = Stopwatch.StartNew();
        menu.Show(standIn.Menu, IntPtr.Zero, Point.Empty, [], customFirst: false, 0, _ => false, null);

        await Assert.That(standIn.Asked).Contains(StandInMenu.Call.GetCommandString);
        await Assert.That(shown).IsEqualTo(1);
        await Assert.That(stopwatch.Elapsed).IsLessThan(waited);
    }

    // One stuck filling in its submenu as it opens, as Send to and Open with are filled in, is
    // cancelled likewise, and the menu goes on.
    [Test]
    public async Task AnExtensionStuckFillingASubmenuIsLeftBehind()
    {
        using var timeouts = new ShortTimeouts();
        using var standIn = StandInMenu.StuckIn(StandInMenu.Call.HandleMenuMsg2, "stuck");
        using var menu = new ShellContextMenu();
        var opened = false;
        menu.Track = (built, _) =>
        {
            // What Windows sends the menu's window as a submenu opens.
            SendMessage(menu.Handle, initMenuPopup, built, IntPtr.Zero);
            opened = true;
            return 0;
        };

        var stopwatch = Stopwatch.StartNew();
        menu.Show(standIn.Menu, IntPtr.Zero, Point.Empty, [], customFirst: false, 0, _ => false, null);

        await Assert.That(standIn.Asked).Contains(StandInMenu.Call.HandleMenuMsg2);
        await Assert.That(opened).IsTrue();
        await Assert.That(stopwatch.Elapsed).IsLessThan(waited);
    }

    // One whose entry, chosen, sticks running is cancelled too, once it has waited the longer
    // time a command is given, and Lode goes on.
    [Test]
    public async Task AnExtensionStuckRunningACommandIsLeftBehind()
    {
        using var timeouts = new ShortTimeouts();
        using var standIn = StandInMenu.StuckIn(StandInMenu.Call.InvokeCommand, "stuck");
        using var menu = new ShellContextMenu();
        menu.Track = (_, _) => standIn.Command("stuck");

        var stopwatch = Stopwatch.StartNew();
        menu.Show(standIn.Menu, IntPtr.Zero, Point.Empty, [], customFirst: false, 0, _ => false, null);

        await Assert.That(standIn.Invoked).IsEquivalentTo(["stuck"]);
        await Assert.That(stopwatch.Elapsed).IsLessThan(waited);
    }

    // A command chosen can rightly keep the thread a while: a dialog it opens on it, a property
    // sheet say, open for as long as it is used. So its time runs only while the thread waits on
    // a call: one the command makes once the dialog is done is not cut short for the time the
    // dialog was open, while one stuck is cancelled once it has waited the time. Nothing the
    // command started is refused for it: it is not known to be what stuck, and the menu built.
    [Test]
    public async Task ACommandsTimeRunsOnlyWhileItWaitsOnACall()
    {
        using var timeouts = new ShortTimeouts();
        var cancels = 0;
        var waiting = false;
        var cancelPendingCall = ShellExtensionGuard.CancelPendingCall;
        var isWaitingOnCall = ShellExtensionGuard.IsWaitingOnCall;
        ShellExtensionGuard.CancelPendingCall = _ =>
        {
            Interlocked.Increment(ref cancels);
            return true;
        };
        ShellExtensionGuard.IsWaitingOnCall = _ => Volatile.Read(ref waiting);
        var started = Guid.NewGuid();
        int inTheDialog;
        try
        {
            using var guard = ShellExtensionGuard.ForCommand();
            guard.RecordActivation(started);
            // Three times the command's time, with no call waiting.
            Thread.Sleep(ShellExtensionGuard.CommandTimeout * 3);
            inTheDialog = Volatile.Read(ref cancels);

            Volatile.Write(ref waiting, true);
            SpinWait.SpinUntil(() => Volatile.Read(ref cancels) > 0, waited);
        }
        finally
        {
            ShellExtensionGuard.CancelPendingCall = cancelPendingCall;
            ShellExtensionGuard.IsWaitingOnCall = isWaitingOnCall;
        }

        await Assert.That(inTheDialog).IsEqualTo(0);
        await Assert.That(cancels).IsGreaterThan(0);
        await Assert.That(ShellExtensionGuard.IsRefused(started)).IsFalse();
    }

    // A call stuck once the menu is built, asking for a verb or filling a submenu, is cancelled,
    // but nothing is refused for it: what was started last, building the menu or since, is not
    // known to be what stuck, and refused would leave its entries off every menu after.
    [Test]
    public async Task NothingIsRefusedForACallStuckOnceTheMenuIsBuilt()
    {
        var started = Guid.NewGuid();
        var cancelPendingCall = ShellExtensionGuard.CancelPendingCall;
        ShellExtensionGuard.CancelPendingCall = _ => true;
        try
        {
            using var guard = ShellExtensionGuard.ForCalls();
            guard.RecordActivation(started);
            guard.CancelCall();
        }
        finally
        {
            ShellExtensionGuard.CancelPendingCall = cancelPendingCall;
        }

        await Assert.That(ShellExtensionGuard.IsRefused(started)).IsFalse();
    }

    // Long enough for the guard to have cancelled the call, at the short timeouts, but far from
    // the twenty seconds the stand-in sticks for.
    static readonly TimeSpan waited = TimeSpan.FromSeconds(5);

    // The guard's timeouts, short, for the test, and as they were after.
    sealed class ShortTimeouts : IDisposable
    {
        TimeSpan timeout = ShellExtensionGuard.Timeout;
        TimeSpan commandTimeout = ShellExtensionGuard.CommandTimeout;

        public ShortTimeouts()
        {
            ShellExtensionGuard.Timeout = TimeSpan.FromMilliseconds(300);
            ShellExtensionGuard.CommandTimeout = TimeSpan.FromMilliseconds(300);
        }

        public void Dispose()
        {
            ShellExtensionGuard.Timeout = timeout;
            ShellExtensionGuard.CommandTimeout = commandTimeout;
        }
    }

    // WM_INITMENUPOPUP.
    const int initMenuPopup = 0x117;

    [DllImport("user32.dll")]
    static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
