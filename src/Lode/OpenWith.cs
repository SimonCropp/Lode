/// <summary>
/// The apps Windows recommends for a type of file, as Explorer's Open with lists them, and
/// Windows' own dialog for choosing another.
/// </summary>
static partial class OpenWith
{
    /// <param name="Name">What Explorer calls it, such as "Firefox".</param>
    /// <param name="Id">The handler's own name, the program's path, by which
    /// <see cref="Open"/> finds it again.</param>
    /// <param name="Icon">Its icon in the system image list.</param>
    public sealed record App(string Name, string Id, int Icon);

    const int recommendedFilter = 0x1;
    const int allowRegistration = 0x1;
    const int execute = 0x4;

    // What Explorer gives no Open with: programs, which an app chosen for them would stop
    // running, and shortcuts, which open what they point at.
    static readonly HashSet<string> excluded =
    [
        with(StringComparer.OrdinalIgnoreCase),
        ".exe",
        ".com",
        ".bat",
        ".cmd",
        ".scr",
        ".pif",
        ".lnk",
        ".url",
        ".appref-ms"
    ];

    /// <summary>
    /// The extension the files share, which Open with lists the apps for: null when they have
    /// different ones, or are programs or shortcuts. Empty when none of them has one.
    /// </summary>
    public static string? Extension(IReadOnlyList<string> paths)
    {
        var extension = Path.GetExtension(paths[0]);
        if (excluded.Contains(extension) ||
            paths.Any(_ => !string.Equals(Path.GetExtension(_), extension, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return extension;
    }

    /// <summary>
    /// The apps recommended for files with the extension, by name.
    /// </summary>
    public static List<App> Find(string extension)
    {
        var apps = new List<App>();
        foreach (var handler in Handlers(extension))
        {
            try
            {
                if (handler.GetName(out var id) != 0 ||
                    handler.GetUIName(out var name) != 0)
                {
                    continue;
                }

                apps.Add(new(name, id, Icon(handler, id)));
            }
            finally
            {
                Marshal.ReleaseComObject(handler);
            }
        }

        apps.Sort((first, second) => StringComparer.CurrentCultureIgnoreCase.Compare(first.Name, second.Name));
        return apps;
    }

    // A packaged app names an image in its package ("@{...}"), which the system image list
    // shows as a blank page. Its program holds the same picture.
    static int Icon(IAssocHandler handler, string id)
    {
        if (handler.GetIconLocation(out var path, out var index) == 0 &&
            path.Length > 0 &&
            !path.StartsWith('@'))
        {
            return ShellIcons.GetResourceIcon(path, index);
        }

        return ShellIcons.GetActualIcon(id);
    }

    /// <summary>
    /// Opens the files, which share an extension, in the app, as Explorer's Open with does.
    /// </summary>
    public static void Open(IWin32Window owner, App app, IReadOnlyList<string> paths)
    {
        var failure = Invoke(owner.Handle, app, paths);
        if (failure != null)
        {
            MessageBox.Show(owner, failure, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Why the app could not open the files, or null once it has. The app is found again rather
    // than kept from when the menu was built, so a menu holds no shell objects.
    static string? Invoke(IntPtr owner, App app, IReadOnlyList<string> paths)
    {
        foreach (var handler in Handlers(Path.GetExtension(paths[0])))
        {
            try
            {
                if (handler.GetName(out var id) != 0 ||
                    !string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dataObject = ShellContextMenu.CreateDataObject(owner, paths);
                if (dataObject == IntPtr.Zero)
                {
                    return "Windows could not find the files to open.";
                }

                try
                {
                    var result = handler.Invoke(dataObject);
                    if (result < 0)
                    {
                        return Marshal.GetExceptionForHR(result)?.Message;
                    }

                    return null;
                }
                finally
                {
                    Marshal.Release(dataObject);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(handler);
            }
        }

        return $"Windows no longer offers {app.Name} for these files.";
    }

    /// <summary>
    /// Windows' Open with dialog for the file, which opens it in the app picked there, and can
    /// make that app the default for its type.
    /// </summary>
    public static void Choose(IntPtr owner, string path)
    {
        fixed (char* file = path)
        {
            var info = new OpenAsInfo
            {
                File = (IntPtr) file,
                Flags = allowRegistration | execute
            };
            SHOpenWithDialog(owner, in info);
        }
    }

    static IEnumerable<IAssocHandler> Handlers(string extension)
    {
        if (SHAssocEnumHandlers(extension, recommendedFilter, out var pointer) != 0 ||
            pointer == IntPtr.Zero)
        {
            yield break;
        }

        var enumerator = (IEnumAssocHandlers)Marshal.GetObjectForIUnknown(pointer);
        Marshal.Release(pointer);
        try
        {
            while (enumerator.Next(1, out var handler, out var fetched) == 0 &&
                   fetched == 1)
            {
                yield return handler;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // OPENASINFO.
    [StructLayout(LayoutKind.Sequential)]
    struct OpenAsInfo
    {
        public IntPtr File;
        public IntPtr Class;
        public int Flags;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHAssocEnumHandlers(string extension, int filter, out IntPtr enumerator);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenWithDialog(IntPtr owner, in OpenAsInfo info);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("973810ae-9599-4b88-9e4d-6ee98c9552da")]
interface IEnumAssocHandlers
{
    // Asked for one at a time, so the array of handlers is a single pointer.
    [PreserveSig]
    int Next(uint count, out IAssocHandler handler, out uint fetched);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("F04061AC-1659-4a3f-A954-775AA57FC083")]
interface IAssocHandler
{
    [PreserveSig]
    int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);

    [PreserveSig]
    int IsRecommended();

    [PreserveSig]
    int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);

    [PreserveSig]
    int Invoke(IntPtr dataObject);

    [PreserveSig]
    int CreateInvoker(IntPtr dataObject, out IntPtr invoker);
}
