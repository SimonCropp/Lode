/// <summary>
/// One running Lode per user session, and one more for launches as administrator. Lode stays in
/// the tray rather than exiting, so a later launch (a folder opened from Explorer, Win+E, the
/// command line) hands its folders to the copy already running and quits, instead of leaving
/// another copy behind.
/// </summary>
sealed class SingleInstance : IDisposable
{
    // Any process may take the foreground for a moment, so the running copy's window can come
    // to the front when this launch hands over. Windows only lets the process the user just
    // started do that.
    const int anyProcess = -1;

    // A Debug build is a copy of its own, so one started from an IDE runs beside the installed
    // Lode rather than handing its folders to it and quitting.
    const string buildSuffix =
#if DEBUG
        "-Debug";
#else
        "";
#endif

    Mutex mutex;
    string pipeName;
    CancelSource cancel = new();

    // What later launches handed over before there was anything to hand it to, and then what to
    // hand it to, and on which thread. Under the gate.
    Lock gate = new();
    List<string[]> waiting = [];
    Action<string[]>? launched;
    SynchronizationContext? context;

    public bool IsFirst { get; }

    public SingleInstance() :
        this(Name())
    {
    }

    internal SingleInstance(string name)
    {
        mutex = new(initiallyOwned: true, $@"Local\{name}", out var createdNew);
        IsFirst = createdNew;
        pipeName = name;
    }

    // A copy run as administrator is a copy of its own as well, beside the one that isn't:
    // Windows lets neither hand its folders to the other, the pipe being only for the same user
    // at the same elevation. Named as the other is, each launch as administrator found the
    // other's mutex, couldn't reach its pipe, and stayed: one more copy every time, none of
    // them able to listen for the next.
    internal static string Name()
    {
        var elevation = "";
        if (Environment.IsPrivilegedProcess)
        {
            elevation = "-Administrator";
        }

        using var process = Process.GetCurrentProcess();
        return $"Lode-{Environment.UserDomainName}-{Environment.UserName}-{process.SessionId}{elevation}{buildSuffix}";
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
    /// Takes later launches' paths from straight after this one became the running copy, so one
    /// made while Lode starts up hands them over, rather than giving up waiting and running
    /// beside it. They are kept until <see cref="HandTo"/>.
    /// </summary>
    public void Listen()
    {
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

                        Received(paths.ToArray());
                    }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // The other launch went away mid-message, or the pipe could not be made
                        // just then. Listening stops for nothing short of Lode closing, or a
                        // later launch would carry on by itself.
                        try
                        {
                            await Task.Delay(200, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            },
            token);
    }

    void Received(string[] paths)
    {
        Action<string[]> handTo;
        SynchronizationContext on;
        lock (gate)
        {
            if (launched == null ||
                context == null)
            {
                waiting.Add(paths);
                return;
            }

            handTo = launched;
            on = context;
        }

        on.Post(_ => handTo(paths), null);
    }

    /// <summary>
    /// Hands each later launch's paths to <paramref name="launched"/>, on the calling (UI)
    /// thread: those that came while Lode was starting up straight away, the rest as they come.
    /// </summary>
    public void HandTo(Action<string[]> launched)
    {
        List<string[]> earlier;
        lock (gate)
        {
            context = SynchronizationContext.Current ?? new SynchronizationContext();
            this.launched = launched;
            earlier = waiting;
            waiting = [];
        }

        foreach (var paths in earlier)
        {
            launched(paths);
        }
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
