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

    sealed class Registration
    {
        public IntPtr Notification;
        public GCHandle Self;
        public readonly List<Action<bool>> Subscribers = [];
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
        }

        return new Subscription(root, changed);
    }

    /// <summary>
    /// A subscription to nothing, for a location with no drive to watch, such as a share.
    /// </summary>
    public static IDisposable None => Subscription.None;

    sealed class Subscription(string root, Action<bool> changed) : IDisposable
    {
        public static readonly IDisposable None = new Subscription("", _ => { });

        bool disposed;

        public void Dispose()
        {
            if (disposed ||
                root.Length == 0)
            {
                return;
            }

            disposed = true;
            Registration? unused = null;
            lock (gate)
            {
                if (registrations.TryGetValue(root, out var registration) &&
                    registration.Subscribers.Remove(changed) &&
                    registration.Subscribers.Count == 0)
                {
                    registrations.Remove(root);
                    unused = registration;
                }
            }

            // Outside the lock: unregistering waits for callbacks under way, which take it.
            if (unused != null)
            {
                CM_Unregister_Notification(unused.Notification);
                unused.Self.Free();
            }
        }
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

        var registration = new Registration();
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

            if (Meaning(action, volumeEvent) is not { } going ||
                GCHandle.FromIntPtr(context).Target is not Registration registration)
            {
                return 0;
            }

            Action<bool>[] subscribers;
            lock (gate)
            {
                subscribers = [.. registration.Subscribers];
            }

            foreach (var subscriber in subscribers)
            {
                subscriber(going);
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
