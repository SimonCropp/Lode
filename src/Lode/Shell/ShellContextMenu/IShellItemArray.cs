[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
interface IShellItemArray
{
    // The first of its methods, and the only one called: those after it are left out.
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
}
