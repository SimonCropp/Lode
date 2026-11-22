/// <summary>
/// Keeps a stuck shell extension from freezing Lode while Windows builds a menu, as Files does.
/// While a guard is in place, a call into an extension running in another process (a packaged
/// app's command, a cloud provider) is cancelled once nothing has been started for longer than
/// <see cref="Timeout"/>, and the menu is built without it. Where a call was cancelled, the
/// extension most recently started is refused for the rest of the session, so later menus don't
/// wait for it. An extension loaded into Lode's own process cannot be stopped this way, and is
/// not refused for being slow: there is no call of its to cancel.
/// </summary>
sealed class ShellExtensionGuard : IDisposable
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

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

    // When the guard was made, or something was last started under it.
    long lastActivity = Environment.TickCount64;
    bool cancelled;
    bool disposed;

    // Created and disposed on the thread making the COM calls.
    public ShellExtensionGuard()
    {
        _ = filter;
        previous = current;
        current = this;
        CoEnableCallCancellation(IntPtr.Zero);
        timer = new(_ => Tick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    void Tick()
    {
        if (Overdue(Environment.TickCount64))
        {
            CancelCall();
        }
    }

    /// <summary>
    /// Whether, at <paramref name="now"/>, nothing has been started for longer than
    /// <see cref="Timeout"/>: a menu that starts one extension after another is slow, not stuck.
    /// </summary>
    internal bool Overdue(long now)
    {
        lock (gate)
        {
            return now - lastActivity >= (long)Timeout.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Cancels the call the thread is waiting on, giving whether there was one. Tests swap it.
    /// </summary>
    internal static Func<uint, bool> CancelPendingCall { get; set; } = static threadId =>
        CoCancelCall(threadId, 0) == ok;

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

            if (!cancelled &&
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

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
