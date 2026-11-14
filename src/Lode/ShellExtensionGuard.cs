/// <summary>
/// Keeps a stuck shell extension from freezing Lode while Windows builds a menu, as Files does.
/// While a guard is in place, a call into an extension running in another process (a packaged
/// app's command, a cloud provider) is cancelled once the guard has been in place longer than
/// <see cref="Timeout"/>, and the menu is built without it. The extension most recently started
/// when that happened is refused for the rest of the session, so later menus don't wait for it.
/// An extension loaded into Lode's own process cannot be stopped this way.
/// </summary>
sealed class ShellExtensionGuard : IDisposable
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    const int ok = 0;
    const int classNotAvailable = unchecked((int)0x80040111);

    // COM takes one filter for the whole process. It only acts on a thread with a guard.
    static readonly ActivationFilter? filter = ActivationFilter.Register();
    static readonly ConcurrentDictionary<Guid, byte> refused = new();

    [ThreadStatic]
    static ShellExtensionGuard? current;

    readonly ShellExtensionGuard? previous;
    readonly uint threadId = GetCurrentThreadId();
    readonly Lock gate = new();
    readonly System.Threading.Timer timer;
    Guid? lastActivation;
    bool cancelled;
    bool disposed;

    // Created and disposed on the thread making the COM calls.
    public ShellExtensionGuard()
    {
        _ = filter;
        previous = current;
        current = this;
        CoEnableCallCancellation(IntPtr.Zero);
        timer = new(_ => CancelCall(), null, Timeout, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Whether extensions of this class are refused, after one held up a menu.
    /// </summary>
    internal static bool IsRefused(Guid classId) =>
        refused.ContainsKey(classId);

    // Every 100ms once past the timeout, so each stuck call in turn is cancelled.
    internal void CancelCall()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (!cancelled &&
                lastActivation is { } classId)
            {
                refused.TryAdd(classId, 0);
            }

            cancelled = true;
            CoCancelCall(threadId, 0);
        }
    }

    internal void RecordActivation(Guid classId)
    {
        lock (gate)
        {
            lastActivation = classId;
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
