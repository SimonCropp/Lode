/// <summary>
/// Tells when a drive is about to go: Safely remove (or another query to remove it), a lock
/// (formatting, a disk check) or a dismount, and when that was called off. Anything holding a
/// handle on the drive, a folder watcher say, must close it at once, or Windows refuses with
/// "this device is currently in use". One registration per drive, shared by every subscriber.
/// </summary>
static unsafe partial class DriveRemoval
{
    // Device notification actions and filter type, from cfgmgr32.h.
    const uint queryRemove = 2;
    const uint queryRemoveFailed = 3;
    const uint removePending = 4;
    const uint removeComplete = 5;
    const uint customEvent = 6;
    const uint filterDeviceHandle = 1;
    const int success = 0;

    // Volume events, from ioevent.h.
    static readonly Guid volumeLock = new("50708874-c9af-11d1-8fef-00a0c9a06d32");
    static readonly Guid volumeLockFailed = new("ae2eed10-0ba8-11d2-8ffb-00a0c9a06d32");
    static readonly Guid volumeUnlock = new("9a8c3d68-d0cb-11d1-8fef-00a0c9a06d32");
    static readonly Guid volumeDismount = new("d16a55e8-1059-11d2-8ffd-00a0c9a06d32");
    static readonly Guid volumeDismountFailed = new("e3c5b178-105d-11d2-8ffd-00a0c9a06d32");
    static readonly Guid volumeMount = new("b5804878-1a96-11d2-8ffd-00a0c9a06d32");

    sealed class Registration(string root)
    {
        public readonly string Root = root;
        public IntPtr Notification;
        public GCHandle Self;
        public readonly List<Action<bool>> Subscribers = [];

        // The device it was registered for has been removed. Under the gate.
        public bool Removed;

        // Under the gate, so it is unregistered once.
        public bool Unregistered;
    }

    static readonly Lock gate = new();
    static readonly Dictionary<string, Registration> registrations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Calls <paramref name="changed"/> with true when <paramref name="root"/>'s drive is about to
    /// go, and false when that was called off or it is back. It is called on a system thread and
    /// must release its handles before returning. Disposing the result stops it.
    /// </summary>
    public static IDisposable Subscribe(string root, Action<bool> changed)
    {
        lock (gate)
        {
            if (!registrations.TryGetValue(root, out var registration))
            {
                registration = Register(root);
                if (registration == null)
                {
                    return Subscription.None;
                }

                registrations[root] = registration;
            }

            registration.Subscribers.Add(changed);
            return new Subscription(registration, changed);
        }
    }

    /// <summary>
    /// A subscription to nothing, for a location with no drive to watch, such as a share.
    /// </summary>
    public static IDisposable None => Subscription.None;

    /// <summary>
    /// Whether <paramref name="subscription"/> is to a device that has since been removed. It
    /// hears nothing more: plugged in again, the drive is another device, which a subscription
    /// made afresh hears of.
    /// </summary>
    public static bool DeviceRemoved(IDisposable subscription)
    {
        lock (gate)
        {
            return subscription is Subscription { Registration.Removed: true };
        }
    }

    sealed class Subscription(Registration? registration, Action<bool> changed) : IDisposable
    {
        public static readonly IDisposable None = new Subscription(null, _ => { });

        public Registration? Registration { get; } = registration;

        bool disposed;

        public void Dispose()
        {
            if (disposed ||
                Registration == null)
            {
                return;
            }

            disposed = true;
            var unused = false;
            lock (gate)
            {
                if (Registration.Subscribers.Remove(changed) &&
                    Registration.Subscribers.Count == 0)
                {
                    Forget(Registration);
                    unused = true;
                }
            }

            // Outside the lock: unregistering waits for callbacks under way, which take it.
            if (unused)
            {
                Unregister(Registration);
            }
        }
    }

    // Under the gate: the next Subscribe for its drive registers afresh.
    static void Forget(Registration registration)
    {
        if (registrations.TryGetValue(registration.Root, out var current) &&
            current == registration)
        {
            registrations.Remove(registration.Root);
        }
    }

    // Not under the gate, nor on a callback's thread: it waits for callbacks under way.
    static void Unregister(Registration registration)
    {
        lock (gate)
        {
            if (registration.Unregistered)
            {
                return;
            }

            registration.Unregistered = true;
        }

        CM_Unregister_Notification(registration.Notification);
        registration.Self.Free();
    }

    // A notification registered against a handle on the drive's root. The registration lasts
    // after the handle is closed, which it is straight away so it doesn't hold the drive itself.
    static Registration? Register(string root)
    {
        using var handle = CreateFileW(root, 0, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var registration = new Registration(root);
        registration.Self = GCHandle.Alloc(registration);
        var filter = new NotifyFilter
        {
            Size = (uint)sizeof(NotifyFilter),
            FilterType = filterDeviceHandle,
            Target = handle.DangerousGetHandle()
        };
        IntPtr notification;
        var result = CM_Register_Notification(&filter, GCHandle.ToIntPtr(registration.Self), &OnNotification, &notification);
        GC.KeepAlive(handle);
        if (result != success)
        {
            registration.Self.Free();
            return null;
        }

        registration.Notification = notification;
        return registration;
    }

    /// <summary>
    /// Whether a notification means the drive is going (true), staying after all (false), or
    /// neither (null).
    /// </summary>
    internal static bool? Meaning(uint action, Guid? volumeEvent)
    {
        switch (action)
        {
            case queryRemove or removePending or removeComplete:
                return true;
            case queryRemoveFailed:
                return false;
            case customEvent when volumeEvent == volumeLock || volumeEvent == volumeDismount:
                return true;
            case customEvent when volumeEvent == volumeLockFailed || volumeEvent == volumeDismountFailed ||
                                  volumeEvent == volumeUnlock || volumeEvent == volumeMount:
                return false;
        }

        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnNotification(IntPtr notification, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            Guid? volumeEvent = null;
            // CM_NOTIFY_EVENT_DATA: the filter type and a reserved field, then the event's GUID.
            if (action == customEvent &&
                eventData != IntPtr.Zero &&
                eventDataSize >= 24)
            {
                volumeEvent = *(Guid*)(eventData + 8);
            }

            if (GCHandle.FromIntPtr(context).Target is Registration registration)
            {
                Dispatch(registration, action, volumeEvent);
            }
        }
        catch (Exception exception)
        {
            // A subscriber is Lode's own, so what it threw is a bug. Windows must get its answer
            // first, so the question is put from elsewhere.
            IssueLauncher.LaunchForExceptionLater(exception.Message, exception);
        }

        return 0;
    }

    static void Dispatch(Registration registration, uint action, Guid? volumeEvent)
    {
        if (Meaning(action, volumeEvent) is not { } going)
        {
            return;
        }

        Action<bool>[] subscribers;
        lock (gate)
        {
            subscribers = [.. registration.Subscribers];

            // The device is gone, and its registration with it: Windows says nothing more to it,
            // not even for the drive plugged in again, which is another device. So the next
            // Subscribe registers afresh, and those subscribed now can tell they must.
            if (action == removeComplete)
            {
                registration.Removed = true;
                Forget(registration);
            }
        }

        foreach (var subscriber in subscribers)
        {
            subscriber(going);
        }

        if (action == removeComplete)
        {
            // Unregistering from the callback would wait on itself.
            ThreadPool.QueueUserWorkItem(_ => Unregister(registration));
        }
    }

    /// <summary>
    /// Acts on a notification as if Windows had sent it for <paramref name="root"/>'s drive, for
    /// tests.
    /// </summary>
    internal static void Notify(string root, uint action)
    {
        Registration? registration;
        lock (gate)
        {
            registrations.TryGetValue(root, out registration);
        }

        if (registration != null)
        {
            Dispatch(registration, action, null);
        }
    }

    /// <summary>
    /// Whether two subscriptions share one registration, for tests.
    /// </summary>
    internal static bool SameRegistration(IDisposable first, IDisposable second) =>
        first is Subscription { Registration: { } registration } &&
        second is Subscription { Registration: { } other } &&
        other == registration;

    // CM_NOTIFY_FILTER: a union of up to 200 wide characters after a 16 byte header.
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public IntPtr Target;
    }

    const uint shareAll = 7;
    const uint openExisting = 3;
    const uint backupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Register_Notification(NotifyFilter* filter, IntPtr context, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> callback, IntPtr* notification);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Unregister_Notification(IntPtr notification);
}
