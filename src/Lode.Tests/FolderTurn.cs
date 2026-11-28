/// <summary>
/// A test run's turn at a fixture's folder, across every test run on the machine. Each run
/// builds its fixtures at the same paths: the fixed one, so captures show the same address bar,
/// and the others named for their test, which verified text holds. Two runs at once, from two
/// checkouts say, would otherwise delete and rebuild each other's folders under them, and each
/// run's last test deleted a folder the other was still showing. A run waits for its turn, and
/// has it till the fixture is disposed.
/// A mutex named for the folder, in the session's namespace, so a run that ends while it has
/// its turn, killed say, lets the next in. Held by a thread of its own, as a mutex can be let go
/// only by the thread that took it, and a test's continuations, and so its Dispose, may run on
/// another.
/// </summary>
sealed class FolderTurn : IDisposable
{
    // Longer than any test takes, so a wait this long is another run stuck.
    static readonly TimeSpan longest = TimeSpan.FromMinutes(5);

    ManualResetEventSlim done = new();
    Thread holder;

    public FolderTurn(string folder)
    {
        var name = Name(folder);
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder = new(() =>
        {
            using var mutex = new Mutex(false, name);
            try
            {
                if (!mutex.WaitOne(longest))
                {
                    taken.SetException(new TimeoutException($"Another test run has had {folder} for {longest.TotalMinutes} minutes."));
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // A run ended while it had its turn: the turn is this one's now.
            }

            taken.SetResult();
            done.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = $"Turn at {folder}"
        };
        holder.Start();
        taken.Task.GetAwaiter().GetResult();
    }

    // The folder's full path, hashed, as a mutex's name can't have a backslash.
    internal static string Name(string folder)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant()));
        return $@"Local\Lode.Tests-{Convert.ToHexString(hash)[..32]}";
    }

    public void Dispose()
    {
        if (done.IsSet)
        {
            return;
        }

        done.Set();
        holder.Join();
        done.Dispose();
    }
}
