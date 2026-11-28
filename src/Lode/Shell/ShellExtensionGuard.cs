/// <summary>
/// Keeps a stuck shell extension from freezing Lode while Windows builds a menu, as Files does,
/// and while Lode then asks the extensions in it for their verbs, has them fill in a submenu, or
/// runs the command chosen. While a guard is in place, a call into an extension running in
/// another process (a packaged app's command, a cloud provider) is cancelled once nothing has
/// been started for longer than <see cref="Timeout"/>, and the menu is built without it. Where a
/// call building the menu was cancelled, the extension most recently started is refused for the
/// rest of the session, so later menus don't wait for it. An extension loaded into Lode's own
/// process cannot be stopped this way, and is not refused for being slow: there is no call of
/// its to cancel.
/// </summary>
sealed class ShellExtensionGuard : IDisposable
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a command chosen from a menu may wait on a call before it is cancelled: far
    /// longer than <see cref="Timeout"/>, as a command can rightly take a while, an app it starts
    /// slow to come up say, or a dialog in the extension's own process open till it is closed.
    /// Only Lode's wait is cancelled; the extension goes on with whatever it was doing.
    /// </summary>
    public static TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    const int ok = 0;
    const int classNotAvailable = unchecked((int)0x80040111);

    // COM takes one filter for the whole process. It only acts on a thread with a guard.
    static ActivationFilter? filter = ActivationFilter.Register();
    static ConcurrentDictionary<Guid, byte> refused = new();

    [ThreadStatic]
    static ShellExtensionGuard? current;

    ShellExtensionGuard? previous;
    uint threadId = GetCurrentThreadId();
    Lock gate = new();
    System.Threading.Timer timer;
    Guid? lastActivation;
    TimeSpan timeout;
    bool refuses;
    bool onlyWhileWaiting;

    // When the guard was made, or something was last started under it, or, for a command, when
    // the thread was last seen not waiting on a call.
    long lastActivity = Environment.TickCount64;
    bool cancelled;
    bool disposed;

    /// <summary>
    /// A guard for building a menu, which refuses what was being started when a call was
    /// cancelled: the shell starts each extension, then has it add its entries, so the one most
    /// recently started is the one stuck.
    /// </summary>
    public ShellExtensionGuard() :
        this(Timeout, refuses: true, onlyWhileWaiting: false)
    {
    }

    /// <summary>
    /// A guard for calls into the extensions of a menu once it is built: asking for their verbs,
    /// and having them fill in a submenu as it opens. They are cancelled as building it is, but
    /// nothing is refused for them: what was started last, building the menu or since, is not
    /// known to be what is stuck, and refused, its entries would be left off every menu after.
    /// </summary>
    public static ShellExtensionGuard ForCalls() =>
        new(Timeout, refuses: false, onlyWhileWaiting: false);

    /// <summary>
    /// A guard for running the command chosen from a menu, which can rightly keep the thread a
    /// while: a dialog it opens on it, a property sheet say, is open for as long as it is used,
    /// and Lode's other windows go on meanwhile. So its time, <see cref="CommandTimeout"/>, runs
    /// only while the thread waits on a call, and a call made once such a dialog is done, by
    /// the command or by another window, is not cut short for the time the dialog was open.
    /// Nothing is refused for a command, which is not known to be what stuck.
    /// </summary>
    public static ShellExtensionGuard ForCommand() =>
        new(CommandTimeout, refuses: false, onlyWhileWaiting: true);

    // Created and disposed on the thread making the COM calls.
    ShellExtensionGuard(TimeSpan timeout, bool refuses, bool onlyWhileWaiting)
    {
        this.timeout = timeout;
        this.refuses = refuses;
        this.onlyWhileWaiting = onlyWhileWaiting;
        _ = filter;
        previous = current;
        current = this;
        CoEnableCallCancellation(IntPtr.Zero);
        timer = new(_ => Tick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    void Tick()
    {
        var now = Environment.TickCount64;
        if (onlyWhileWaiting &&
            !IsWaitingOnCall(threadId))
        {
            lock (gate)
            {
                lastActivity = now;
            }

            return;
        }

        if (Overdue(now))
        {
            CancelCall();
        }
    }

    /// <summary>
    /// Whether, at <paramref name="now"/>, nothing has been started for longer than the guard's
    /// timeout: a menu that starts one extension after another is slow, not stuck.
    /// </summary>
    internal bool Overdue(long now)
    {
        lock (gate)
        {
            return now - lastActivity >= (long)timeout.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Cancels the call the thread is waiting on, giving whether there was one. Tests swap it.
    /// </summary>
    internal static Func<uint, bool> CancelPendingCall { get; set; } = static threadId =>
        CoCancelCall(threadId, 0) == ok;

    static Guid cancelMethodCallsId = new("00000029-0000-0000-C000-000000000046");

    /// <summary>
    /// Whether the thread is waiting on a call it made, which, with cancelling turned on, has COM
    /// keep an object to cancel it by till it returns. Tests swap it.
    /// </summary>
    internal static Func<uint, bool> IsWaitingOnCall { get; set; } = static threadId =>
    {
        var interfaceId = cancelMethodCallsId;
        if (CoGetCancelObject(threadId, ref interfaceId, out var cancel) != ok)
        {
            return false;
        }

        Marshal.Release(cancel);
        return true;
    };

    /// <summary>
    /// Whether extensions of this class are refused, after one held up a menu.
    /// </summary>
    internal static bool IsRefused(Guid classId) =>
        refused.ContainsKey(classId);

    // Every 100ms once overdue, so each stuck call in turn is cancelled. Only one that was
    // cancelled gets what was last started refused: with no call waiting, the menu is merely
    // slow to build, a first one say, or held up by an extension in Lode's own process.
    internal void CancelCall()
    {
        lock (gate)
        {
            if (disposed ||
                !CancelPendingCall(threadId))
            {
                return;
            }

            if (refuses &&
                !cancelled &&
                lastActivation is { } classId)
            {
                refused.TryAdd(classId, 0);
            }

            cancelled = true;
        }
    }

    internal void RecordActivation(Guid classId)
    {
        lock (gate)
        {
            lastActivation = classId;
            lastActivity = Environment.TickCount64;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }

        timer.Dispose();
        CoDisableCallCancellation(IntPtr.Zero);
        current = previous;
    }

    static int HandleActivation(ref Guid classId, out Guid replacement)
    {
        replacement = classId;
        if (current is not { } guard)
        {
            return ok;
        }

        if (refused.ContainsKey(classId))
        {
            return classNotAvailable;
        }

        guard.RecordActivation(classId);
        return ok;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000017-0000-0000-C000-000000000046")]
    interface IActivationFilter
    {
        [PreserveSig]
        int HandleActivation(uint activationType, ref Guid classId, out Guid replacementClassId);
    }

    // Called on the thread doing the activation, so the guard it finds is that thread's.
    [ComVisible(true)]
    sealed class ActivationFilter : IActivationFilter
    {
        public static ActivationFilter? Register()
        {
            var created = new ActivationFilter();
            try
            {
                if (CoRegisterActivationFilter(created) >= 0)
                {
                    return created;
                }
            }
            catch (Exception)
            {
                // Without the filter the timeout still applies.
            }

            return null;
        }

        public int HandleActivation(uint activationType, ref Guid classId, out Guid replacementClassId) =>
            ShellExtensionGuard.HandleActivation(ref classId, out replacementClassId);
    }

    [DllImport("ole32.dll")]
    static extern int CoRegisterActivationFilter([MarshalAs(UnmanagedType.Interface)] IActivationFilter filter);

    [DllImport("ole32.dll")]
    static extern int CoEnableCallCancellation(IntPtr reserved);

    [DllImport("ole32.dll")]
    static extern int CoDisableCallCancellation(IntPtr reserved);

    [DllImport("ole32.dll")]
    static extern int CoCancelCall(uint threadId, uint timeout);

    [DllImport("ole32.dll")]
    static extern int CoGetCancelObject(uint threadId, ref Guid interfaceId, out IntPtr cancel);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
