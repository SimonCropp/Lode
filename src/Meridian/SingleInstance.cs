using System.IO.Pipes;

/// <summary>
/// One running Meridian per user session. Meridian stays in the tray rather than exiting, so a
/// later launch (a folder opened from Explorer, Win+E, the command line) hands its folders to
/// the copy already running and quits, instead of leaving another copy behind.
/// </summary>
sealed class SingleInstance : IDisposable
{
    // Any process may take the foreground for a moment, so the running copy's window can come
    // to the front when this launch hands over. Windows only lets the process the user just
    // started do that.
    const int anyProcess = -1;

    readonly Mutex mutex;
    readonly string pipeName;
    readonly CancelSource cancel = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        using var process = Process.GetCurrentProcess();
        var name = $"Meridian-{Environment.UserDomainName}-{Environment.UserName}-{process.SessionId}";
        mutex = new(initiallyOwned: true, $@"Local\{name}", out var createdNew);
        IsFirst = createdNew;
        pipeName = name;
    }

    /// <summary>
    /// Sends the folders and files to open to the running copy. False when it could not be
    /// reached, in which case this launch should carry on by itself.
    /// </summary>
    public bool TryHandOff(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            AllowSetForegroundWindow(anyProcess);
            using var writer = new StreamWriter(client);

            // A line each: paths cannot contain line breaks.
            foreach (var path in paths)
            {
                writer.WriteLine(path);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands each later launch's paths to <paramref name="launched"/>, on the calling (UI) thread.
    /// </summary>
    public void Listen(Action<string[]> launched)
    {
        var context = SynchronizationContext.Current!;
        var token = cancel.Token;
        Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await using var server = new NamedPipeServerStream(
                            pipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server);
                        var paths = new List<string>();
                        while (await reader.ReadLineAsync(token) is { } line)
                        {
                            paths.Add(line);
                        }

                        context.Post(_ => launched(paths.ToArray()), null);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        // The other launch went away mid-message; wait for the next.
                    }
                }
            },
            token);
    }

    public void Dispose()
    {
        cancel.Cancel();
        cancel.Dispose();
        if (IsFirst)
        {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }
}
