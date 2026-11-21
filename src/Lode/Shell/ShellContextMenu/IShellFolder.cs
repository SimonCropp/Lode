[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E6-0000-0000-C000-000000000046")]
interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(IntPtr owner, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eaten, out IntPtr identifier, IntPtr attributes);

    [PreserveSig]
    int EnumObjects(IntPtr owner, int flags, out IntPtr enumerator);

    [PreserveSig]
    int BindToObject(IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int BindToStorage(IntPtr identifier, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);

    [PreserveSig]
    int CreateViewObject(IntPtr owner, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] identifiers, ref uint attributes);

    [PreserveSig]
    int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] identifiers, ref Guid interfaceId, IntPtr reserved, out IntPtr result);

    [PreserveSig]
    int GetDisplayNameOf(IntPtr identifier, uint flags, IntPtr name);

    [PreserveSig]
    int SetNameOf(IntPtr owner, IntPtr identifier, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr result);
}