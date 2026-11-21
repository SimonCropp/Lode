[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E4-0000-0000-C000-000000000046")]
interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

    [PreserveSig]
    int InvokeCommand(ref InvokeCommandInfo info);

    [PreserveSig]
    int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint length);
}