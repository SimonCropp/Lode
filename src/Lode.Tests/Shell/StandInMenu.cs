/// <summary>
/// A shell extension's menu, made up for a test, with an entry for each verb it is given, as
/// Lode gets one from the shell: <see cref="Menu"/> is the shell's menu, on Lode's thread, which
/// calls the extension for what it added. The extension is on a thread of its own, in an
/// apartment of its own, as one in another process is to Lode, so each call into it is COM's,
/// through its proxy, as the guard on shell extensions can cancel.
/// </summary>
sealed class StandInMenu : IDisposable
{
    Thread thread;
    uint threadId;
    IContextMenu3 extension;
    Extension standIn;

    StandInMenu(IReadOnlyList<string> verbs)
    {
        standIn = new(verbs);
        using var started = new ManualResetEventSlim();
        var stream = IntPtr.Zero;
        var marshalled = 0;
        thread = new(
            () =>
            {
                threadId = GetCurrentThreadId();
                var pointer = Marshal.GetComInterfaceForObject(standIn, typeof(IContextMenu3));
                var interfaceId = typeof(IContextMenu3).GUID;
                marshalled = CoMarshalInterThreadInterfaceInStream(ref interfaceId, pointer, out stream);
                Marshal.Release(pointer);
                started.Set();
                Application.Run();
            })
        {
            IsBackground = true,
            Name = "Stand-in shell extension"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Wait();
        Marshal.ThrowExceptionForHR(marshalled);

        var proxyId = typeof(IContextMenu3).GUID;
        Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, ref proxyId, out var proxy));
        extension = (IContextMenu3)Marshal.GetObjectForIUnknown(proxy);
        Marshal.Release(proxy);
        Menu = new ShellMenu(extension, verbs);
    }

    public static StandInMenu Start(params string[] verbs) =>
        new(verbs);

    /// <summary>
    /// The shell's menu, which Lode is given and calls on its own thread.
    /// </summary>
    public IContextMenu Menu { get; }

    /// <summary>
    /// The command Lode was given for the entry with this verb, once the menu has been built.
    /// </summary>
    public uint Command(string verb) =>
        standIn.FirstCommand + (uint)standIn.Verbs.ToList().IndexOf(verb);

    /// <summary>
    /// The verbs of the entries the extension was asked to run, in turn.
    /// </summary>
    public IReadOnlyList<string> Invoked
    {
        get
        {
            lock (standIn.Invoked)
            {
                return standIn.Invoked.ToList();
            }
        }
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(extension);
        PostThreadMessage(threadId, quitMessage, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(10));
    }

    const uint quitMessage = 0x12;
    // GCS_VERBW.
    const uint verbUnicode = 0x4;

    // What the shell's menu is to Lode: it calls each extension for the entries it added, as
    // this does the one, through COM's proxy for it. The proxy carries back only the first
    // character of what GetCommandString gives, so the verb, once the extension has answered,
    // is the one this knows the entry by, and the proxy has to be given somewhere to put what
    // GetCommandString leaves unused, which Lode gives none.
    sealed class ShellMenu(IContextMenu3 extension, IReadOnlyList<string> verbs) : IContextMenu, IContextMenu2, IContextMenu3
    {
        static IntPtr reserved = Zeroed();

        static IntPtr Zeroed()
        {
            var pointer = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(pointer, 0);
            return pointer;
        }

        public int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags) =>
            extension.QueryContextMenu(menu, indexMenu, firstCommand, lastCommand, flags);

        public int InvokeCommand(ref InvokeCommandInfo info) =>
            extension.InvokeCommand(ref info);

        public int GetCommandString(UIntPtr command, uint flags, IntPtr unused, IntPtr name, uint length)
        {
            var answer = extension.GetCommandString(command, flags, reserved, name, length);
            if (answer != 0)
            {
                return answer;
            }

            var verb = verbs[(int)command] + '\0';
            Marshal.Copy(verb.ToCharArray(), 0, name, verb.Length);
            return 0;
        }

        public int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam) =>
            extension.HandleMenuMsg(message, wParam, lParam);

        public int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result) =>
            extension.HandleMenuMsg2(message, wParam, lParam, out result);
    }

    // The extension, on its own thread. Refusing IMarshal has COM give Lode its proxy, as it
    // would for an extension in another process, rather than the object itself, as .NET's own
    // marshaller would.
    sealed class Extension(IReadOnlyList<string> verbs) :
        IContextMenu,
        IContextMenu2,
        IContextMenu3,
        ICustomQueryInterface
    {
        static Guid marshalId = new("00000003-0000-0000-C000-000000000046");
        static Guid agileId = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

        public IReadOnlyList<string> Verbs => verbs;
        public uint FirstCommand { get; private set; }
        public List<string> Invoked { get; } = [];

        public int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags)
        {
            FirstCommand = firstCommand;
            for (var index = 0; index < verbs.Count; index++)
            {
                AppendMenu(menu, 0, (UIntPtr)(firstCommand + index), verbs[index]);
            }

            return verbs.Count;
        }

        public int InvokeCommand(ref InvokeCommandInfo info)
        {
            // Lode runs an entry by its offset, as its menu gave it.
            var offset = (int)info.Verb;
            lock (Invoked)
            {
                Invoked.Add(verbs[offset]);
            }

            return 0;
        }

        // Answers for its own entries' verbs, which the shell's menu gives Lode.
        public int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint length)
        {
            if (flags != verbUnicode ||
                (int)command >= verbs.Count)
            {
                return unchecked((int)0x80004001);
            }

            return 0;
        }

        public int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam) =>
            0;

        public int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            return 0;
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid interfaceId, out IntPtr pointer)
        {
            pointer = IntPtr.Zero;
            if (interfaceId == marshalId ||
                interfaceId == agileId)
            {
                return CustomQueryInterfaceResult.Failed;
            }

            return CustomQueryInterfaceResult.NotHandled;
        }
    }

    [DllImport("ole32.dll")]
    static extern int CoMarshalInterThreadInterfaceInStream(ref Guid interfaceId, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid interfaceId, out IntPtr result);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
}
