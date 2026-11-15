/// <summary>
/// Changes reported from any thread, often in bursts, turned into at most one call on the UI
/// thread per interval. Nothing ticks while nothing is reported, so a window left idle, or in
/// the tray, is never woken for it. Made on the UI thread.
/// </summary>
sealed class ChangeSignal : IDisposable
{
    // Reaches the UI thread whether or not any window of its controls has been created yet.
    readonly SynchronizationContext context = new WindowsFormsSynchronizationContext();
    readonly Timer timer;
    readonly Func<bool> changed;
    int raised;
    bool disposed;

    /// <param name="changed">Called on the UI thread, an interval after a change was reported.
    /// False when the change has to wait, to be called again after another interval.</param>
    public ChangeSignal(int interval, Func<bool> changed)
    {
        this.changed = changed;
        timer = new()
        {
            Interval = interval
        };
        timer.Tick += (_, _) => OnTick();
    }

    /// <summary>
    /// Reports a change, from any thread.
    /// </summary>
    public void Raise()
    {
        // Only the first of a burst has a timer to start.
        if (Interlocked.Exchange(ref raised, 1) == 0)
        {
            context.Post(_ => Start(), null);
        }
    }

    /// <summary>
    /// Forgets a change reported and not yet dealt with.
    /// </summary>
    public void Reset()
    {
        timer.Stop();
        Volatile.Write(ref raised, 0);
    }

    void Start()
    {
        if (!disposed)
        {
            timer.Start();
        }
    }

    void OnTick()
    {
        // Lowered before the call, so a change reported during it starts the timer again.
        Reset();
        if (changed())
        {
            return;
        }

        Volatile.Write(ref raised, 1);
        timer.Start();
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
    }
}
