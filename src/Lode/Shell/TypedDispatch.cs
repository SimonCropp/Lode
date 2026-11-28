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
        var typeInfo = TypeInfo(interfaceType.GUID, library, major, minor);
        if (typeInfo == IntPtr.Zero)
        {
            return null;
        }

        var outer = Marshal.GetIUnknownForObject(owner);

        // Past the owner's ICustomQueryInterface, which can turn the interface down to others.
        var implementation = Marshal.GetComInterfaceForObject(owner, interfaceType, CustomQueryInterfaceMode.Ignore);
        try
        {
            // Neither is kept counted: the standard IDispatch is part of the object, which holds
            // it, so lives no longer than the object does. The type information it counts itself.
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
            Marshal.Release(typeInfo);
        }
    }

    // The interface's type information, counted, or zero where Windows hasn't the library or the
    // interface in it. Had as Windows' own pointers, not through .NET: Windows loads a library
    // once for every thread, and .NET keeps one object for each of Windows', tied to the thread
    // that first had it, so a second thread, each test's own say, was handed it through COM's
    // marshalling, as its type information, in which no name can be looked up: asked, it says it
    // isn't implemented, and so did the standard IDispatch, for every name a script asked for.
    static unsafe IntPtr TypeInfo(Guid interfaceId, Guid library, ushort major, ushort minor)
    {
        if (LoadRegTypeLib(ref library, major, minor, 0, out var typeLibrary) < 0)
        {
            return IntPtr.Zero;
        }

        IntPtr typeInfo;
        try
        {
            // ITypeLib::GetTypeInfoOfGuid, after IUnknown's three methods and three of its own.
            var getTypeInfoOfGuid = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Method(typeLibrary, 6);
            if (getTypeInfoOfGuid(typeLibrary, &interfaceId, &typeInfo) < 0)
            {
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.Release(typeLibrary);
        }

        return VtableOf(typeInfo);
    }

    // A dual interface is found as its dispatch half, which says nothing of where each method
    // is in the vtable the standard IDispatch calls it through. Its vtable half does. Takes the
    // count of what it is given, and gives one counted.
    static unsafe IntPtr VtableOf(IntPtr typeInfo)
    {
        // ITypeInfo::GetRefTypeOfImplType and GetRefTypeInfo.
        var getRefTypeOfImplType = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)Method(typeInfo, 8);
        var getRefTypeInfo = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Method(typeInfo, 14);
        uint vtable;
        IntPtr vtableInfo;
        if (getRefTypeOfImplType(typeInfo, unchecked((uint)-1), &vtable) < 0 ||
            getRefTypeInfo(typeInfo, vtable, &vtableInfo) < 0)
        {
            // Not dual: an interface that is only a vtable already.
            return typeInfo;
        }

        Marshal.Release(typeInfo);
        return vtableInfo;
    }

    // A method of a COM interface, by its place in the interface's vtable.
    static unsafe IntPtr Method(IntPtr pointer, int slot) =>
        (*(IntPtr**)pointer)[slot];

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
    static extern int LoadRegTypeLib(ref Guid library, ushort major, ushort minor, int locale, out IntPtr typeLibrary);

    [DllImport("oleaut32.dll")]
    static extern int CreateStdDispatch(IntPtr outer, IntPtr implementation, IntPtr typeInfo, out IntPtr standard);
}
