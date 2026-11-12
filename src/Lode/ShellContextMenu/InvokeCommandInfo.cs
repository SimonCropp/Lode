[StructLayout(LayoutKind.Sequential)]
struct InvokeCommandInfo
{
    public int Size;
    public uint Mask;
    public IntPtr Window;
    public IntPtr Verb;
    public IntPtr Parameters;
    public IntPtr Directory;
    public int Show;
    public uint HotKey;
    public IntPtr Icon;
    public IntPtr Title;
    public IntPtr VerbUnicode;
    public IntPtr ParametersUnicode;
    public IntPtr DirectoryUnicode;
    public IntPtr TitleUnicode;
    public Point InvokePoint;
}