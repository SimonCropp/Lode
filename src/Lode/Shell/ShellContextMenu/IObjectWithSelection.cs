[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C")]
interface IObjectWithSelection
{
    // Takes an IShellItemArray.
    [PreserveSig]
    int SetSelection(IntPtr items);

    [PreserveSig]
    int GetSelection(ref Guid interfaceId, out IntPtr result);
}
