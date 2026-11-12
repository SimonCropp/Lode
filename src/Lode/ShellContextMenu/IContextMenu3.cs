[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
interface IContextMenu3
{
    [PreserveSig]
    int QueryContextMenu(IntPtr menu, uint indexMenu, uint firstCommand, uint lastCommand, uint flags);

    [PreserveSig]
    int InvokeCommand(ref InvokeCommandInfo info);

    [PreserveSig]
    int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint length);

    [PreserveSig]
    int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);

    [PreserveSig]
    int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
}