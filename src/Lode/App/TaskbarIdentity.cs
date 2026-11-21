/// <summary>
/// What Lode's windows tell the taskbar about themselves, so that a pin made from one keeps
/// the folder icon once Lode is closed.
/// </summary>
/// <remarks>
/// A pin is a shortcut, and a shortcut shows its target's icon. Lode's target is the shim
/// dotnet tool install makes (%USERPROFILE%\.dotnet\tools\lode.exe), which has no icon, so a
/// pin to it shows Windows' blank program icon whenever Lode is closed; while Lode runs, the
/// button shows the window's own icon, the folder. A window can instead give the shortcut that
/// pinning makes a command, name and icon of its own, and the icon here is Windows' folder
/// icon, named where Windows keeps it rather than copied into Lode. The taskbar reads these
/// only from a window with an AppUserModelID of its own, which also decides which pin the
/// window's button joins, and which jump list it shows (see JumpList): a pin made before
/// Lode's windows had one is not theirs, and has to be unpinned and Lode pinned again.
/// </remarks>
static partial class TaskbarIdentity
{
    public const string AppId = "Lode";

    // The System.AppUserModel properties.
    static readonly Guid appUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static readonly PropertyStore.PropertyKey relaunchCommandKey = new(appUserModel, 2);
    static readonly PropertyStore.PropertyKey relaunchIconKey = new(appUserModel, 3);
    static readonly PropertyStore.PropertyKey relaunchNameKey = new(appUserModel, 4);
    static readonly PropertyStore.PropertyKey idKey = new(appUserModel, 5);

    // SHSTOCKICONID: Windows' own icons for a folder, a machine on the network and a share.
    public const uint StockFolder = 3;
    public const uint StockServer = 15;
    public const uint StockServerShare = 51;
    const uint stockIconLocation = 0;

    public static void Apply(IntPtr window) =>
        Apply(window, ExplorerReplacement.PreferredCommand(), FolderIconLocation());

    public static void Apply(IntPtr window, string command, string? icon) =>
        Change(
            window,
            _ =>
            {
                PropertyStore.Set(_, relaunchCommandKey, command);
                PropertyStore.Set(_, relaunchNameKey, AppId);
                if (icon != null)
                {
                    PropertyStore.Set(_, relaunchIconKey, icon);
                }

                PropertyStore.Set(_, idKey, AppId);
                _.Commit();
            });

    /// <summary>
    /// Takes the properties off a window that is going away. The shell keeps them with the
    /// window, and lets go of them only when they are emptied.
    /// </summary>
    public static void Clear(IntPtr window) =>
        Change(
            window,
            _ =>
            {
                PropertyStore.Empty(_, idKey);
                PropertyStore.Empty(_, relaunchCommandKey);
                PropertyStore.Empty(_, relaunchNameKey);
                PropertyStore.Empty(_, relaunchIconKey);
                _.Commit();
            });

    public record Identity(string? Id, string? Command, string? Name, string? Icon);

    public static Identity Read(IntPtr window)
    {
        Identity? identity = null;
        Change(
            window,
            _ => identity = new(
                PropertyStore.Get(_, idKey),
                PropertyStore.Get(_, relaunchCommandKey),
                PropertyStore.Get(_, relaunchNameKey),
                PropertyStore.Get(_, relaunchIconKey)));
        return identity ?? new(null, null, null, null);
    }

    /// <summary>
    /// Where Windows keeps its folder icon, as a shortcut names an icon: the file, a comma, then
    /// the icon's index in it (a resource id when negative). Null if Windows does not say.
    /// </summary>
    public static string? FolderIconLocation()
    {
        if (StockIcon(StockFolder) is not { } icon)
        {
            return null;
        }

        return $"{icon.File},{icon.Index}";
    }

    /// <summary>
    /// Where Windows keeps one of its own icons, a <c>SIID_</c> value: the file, and the icon's
    /// index in it (a resource id when negative). Null if Windows does not say.
    /// </summary>
    public static (string File, int Index)? StockIcon(uint id)
    {
        var info = new StockIconInfo
        {
            Size = (uint)Marshal.SizeOf<StockIconInfo>()
        };
        if (SHGetStockIconInfo(id, stockIconLocation, ref info) != 0)
        {
            return null;
        }

        var path = info.Path;
        if (path.Length == 0)
        {
            return null;
        }

        return (path, info.IconIndex);
    }

    // Where the taskbar is a nicety, so a window it cannot be told about is left as it is.
    static void Change(IntPtr window, Action<PropertyStore.IPropertyStore> change)
    {
        if (SHGetPropertyStoreForWindow(window, typeof(PropertyStore.IPropertyStore).GUID, out var store) != 0)
        {
            return;
        }

        try
        {
            change(store);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // SHSTOCKICONINFO. The path is a fixed buffer of UTF-16 units, as in ShellFileInfo.
    [StructLayout(LayoutKind.Sequential)]
    struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SystemImageIndex;
        public int IconIndex;
        PathBuffer path;

        public readonly string Path => Text(path);

        static string Text(ReadOnlySpan<ushort> raw)
        {
            var buffer = MemoryMarshal.Cast<ushort, char>(raw);
            var end = buffer.IndexOf((char)0);
            if (end < 0)
            {
                return new(buffer);
            }

            return new(buffer[..end]);
        }

        [InlineArray(260)]
        struct PathBuffer
        {
            ushort first;
        }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(
        IntPtr window,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out PropertyStore.IPropertyStore store);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);
}
