using TUnit.Core.Interfaces;

/// <summary>
/// Runs a test, and the hooks of its class, as STAThreadExecutor does: each on a thread of its
/// own, in the single-threaded apartment windows need. And fails the test on anything thrown in
/// a window's message handler on that thread. WinForms catches such an exception and hands it
/// to the thread's Application.ThreadException handler, or, with none, puts up its error dialog,
/// which waits for somebody to close it, and the run with it. The handler is per thread, so it
/// is set on each test's, before the test, rather than once for the run: set on the thread the
/// run starts on, as Application.SetUnhandledExceptionMode was, it covered no test.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
sealed class WindowThreadAttribute :
    Attribute,
    ITestRegisteredEventReceiver,
    IHookRegisteredEventReceiver,
    IScopedAttribute
{
    WindowThreadExecutor executor = new();

    public int Order => 0;

    // One executor a test: a method's, over its class's.
    public Type ScopeType => typeof(ITestExecutor);

    public ValueTask OnTestRegistered(TestRegisteredContext context)
    {
        context.SetTestExecutor(executor);
        context.SetHookExecutor(executor);
        return default;
    }

    public ValueTask OnHookRegistered(HookRegisteredContext context)
    {
        context.HookExecutor = executor;
        return default;
    }
}

sealed class WindowThreadExecutor : STAThreadExecutor
{
    // The thread's, while its test or hook runs on it. Each has a thread of its own, so this
    // executor, shared by a class's tests, keeps nothing of one test where another sees it.
    [ThreadStatic]
    static WindowErrors? errors;

    protected override void Initialize() =>
        errors = new();

    protected override void CleanUp()
    {
        var kept = errors!;
        errors = null;
        kept.Dispose();
        if (kept.Thrown.Count > 0)
        {
            throw new AggregateException(
                "Thrown on the test's thread where the test couldn't see it: in a window's message handler, where WinForms would have put up its error dialog, or for a box Lode put up",
                kept.Thrown);
        }
    }

    /// <summary>
    /// Keeps <paramref name="exception"/> for the test on this thread to fail with, should
    /// whatever it is thrown to swallow it, as the test thread's synchronization context does
    /// what an async void method throws. Kept only on a window test's thread. Answers the
    /// exception, to be thrown.
    /// </summary>
    internal static Exception Keep(Exception exception)
    {
        errors?.Keep(exception);
        return exception;
    }

    /// <summary>
    /// What was thrown in a window's message handler on this thread while it was kept, and what
    /// else was kept, for the test to fail on. Application.ThreadException keeps one handler a
    /// thread, and adding one replaces whatever the thread had, so nothing else on the thread
    /// sets it meanwhile.
    /// </summary>
    sealed class WindowErrors : IDisposable
    {
        public List<Exception> Thrown { get; } = [];

        public WindowErrors() =>
            Application.ThreadException += Keep;

        void Keep(object sender, ThreadExceptionEventArgs args) =>
            Keep(args.Exception);

        // Once each: one kept as it was thrown can reach a window's handler too.
        public void Keep(Exception exception)
        {
            if (!Thrown.Contains(exception))
            {
                Thrown.Add(exception);
            }
        }

        public void Dispose() =>
            Application.ThreadException -= Keep;
    }
}
