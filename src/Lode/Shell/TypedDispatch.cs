using ComTypeInfo = System.Runtime.InteropServices.ComTypes.ITypeInfo;
using ComTypeLibrary = System.Runtime.InteropServices.ComTypes.ITypeLib;

/// <summary>
/// IDispatch for one of Lode's COM objects that stands in for one of Windows', described by the
/// type library Windows describes its own by: what a script, PowerShell, VBScript or AutoHotkey,
/// calls by name. .NET's own IDispatch for such an object knows only the names its methods have
/// in Lode, get_LocationURL rather than LocationURL, and gives no type information, without
/// which PowerShell sees no property at all. So the object hands out this instead, from
/// <see cref="ICustomQueryInterface"/>: Windows' standard IDispatch (CreateStdDispatch), which
/// looks names up in the type library, says what they are to whoever asks for the type
/// information, and calls the object's own methods through the interface's vtable, laid out by
/// .NET as Lode declares it, every method in its place. It is aggregated by the object, so asked
/// for anything else, or counted, it hands that to the object.
/// </summary>
sealed class TypedDispatch
{
    static Guid dispatchId = new("00020400-0000-0000-C000-000000000046");

    // The standard IDispatch's own IUnknown, the one reference that keeps it.
    readonly IntPtr inner;

    TypedDispatch(IntPtr inner) =>
        this.inner = inner;

    // Once nothing holds the object, nothing can be calling this either: whoever had its
    // IDispatch held the object by it.
    ~TypedDispatch() =>
        Marshal.Release(inner);

    /// <summary>
    /// The IDispatch for <paramref name="owner"/>'s <paramref name="interfaceType"/>, as the
    /// registered type library <paramref name="library"/> describes it, or null where Windows
    /// doesn't have that, when <paramref name="owner"/> keeps .NET's own.
    /// </summary>
    public static TypedDispatch? Create(object owner, Type interfaceType, Guid library, ushort major, ushort minor)
    {
        ComTypeInfo typeInfo;
        try
        {
            if (LoadRegTypeLib(ref library, major, minor, 0, out var typeLibrary) < 0)
            {
                return null;
            }

            var interfaceId = interfaceType.GUID;
            typeLibrary.GetTypeInfoOfGuid(ref interfaceId, out typeInfo);
            typeInfo = VtableOf(typeInfo);
        }
        catch (COMException)
        {
            return null;
        }

        var outer = Marshal.GetIUnknownForObject(owner);

        // Past the owner's ICustomQueryInterface, which can turn the interface down to others.
        var implementation = Marshal.GetComInterfaceForObject(owner, interfaceType, CustomQueryInterfaceMode.Ignore);
        try
        {
            // Neither is kept counted: the standard IDispatch is part of the object, which holds
            // it, so lives no longer than the object does.
            if (CreateStdDispatch(outer, implementation, typeInfo, out var inner) < 0)
            {
                return null;
            }

            return new(inner);
        }
        finally
        {
            Marshal.Release(implementation);
            Marshal.Release(outer);
        }
    }

    // A dual interface is found as its dispatch half, which says nothing of where each method
    // is in the vtable the standard IDispatch calls it through. Its vtable half does.
    static ComTypeInfo VtableOf(ComTypeInfo typeInfo)
    {
        try
        {
            typeInfo.GetRefTypeOfImplType(-1, out var vtable);
            typeInfo.GetRefTypeInfo(vtable, out var vtableInfo);
            return vtableInfo;
        }
        catch (COMException)
        {
            // Not dual: an interface that is only a vtable already.
            return typeInfo;
        }
    }

    /// <summary>
    /// The IDispatch, counted, for the object's <see cref="ICustomQueryInterface"/> to hand out.
    /// </summary>
    public CustomQueryInterfaceResult QueryInterface(out IntPtr result)
    {
        if (Marshal.QueryInterface(inner, in dispatchId, out result) < 0)
        {
            return CustomQueryInterfaceResult.Failed;
        }

        return CustomQueryInterfaceResult.Handled;
    }

    /// <summary>
    /// Whether <paramref name="interfaceId"/> is IDispatch's.
    /// </summary>
    public static bool IsDispatch(Guid interfaceId) =>
        interfaceId == dispatchId;

    /// <summary>
    /// <paramref name="owner"/>'s IDispatch, counted, as the shell is to be given it: asked of
    /// it as COM would, rather than as .NET's marshalling does, which can skip what
    /// <see cref="ICustomQueryInterface"/> answers.
    /// </summary>
    public static IntPtr DispatchOf(object owner)
    {
        var unknown = Marshal.GetIUnknownForObject(owner);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in dispatchId, out var dispatch));
            return dispatch;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [DllImport("oleaut32.dll")]
    static extern int LoadRegTypeLib(ref Guid library, ushort major, ushort minor, int locale, out ComTypeLibrary typeLibrary);

    [DllImport("oleaut32.dll")]
    static extern int CreateStdDispatch(IntPtr outer, IntPtr implementation, [MarshalAs(UnmanagedType.Interface)] ComTypeInfo typeInfo, out IntPtr standard);
}
