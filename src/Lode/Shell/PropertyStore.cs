/// <summary>
/// Reads and writes the shell's properties on what has them: a window's, which tell the taskbar
/// about it (see TaskbarIdentity), and a shortcut's, which name an entry in the jump list (see
/// JumpList). Only for the string properties those use.
/// </summary>
static partial class PropertyStore
{
    const ushort emptyType = 0;
    const ushort stringType = 31;

    public static void Set(IPropertyStore store, PropertyKey key, string text)
    {
        var value = new PropVariant
        {
            Type = stringType,
            Value = Marshal.StringToCoTaskMemUni(text)
        };
        try
        {
            store.SetValue(ref key, ref value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Value);
        }
    }

    public static void Empty(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant
        {
            Type = emptyType
        };
        store.SetValue(ref key, ref value);
    }

    public static string? Get(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0)
        {
            return null;
        }

        try
        {
            if (value.Type != stringType)
            {
                return null;
            }

            return Marshal.PtrToStringUni(value.Value);
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // PROPERTYKEY.
    [StructLayout(LayoutKind.Sequential)]
    // ReSharper disable NotAccessedPositionalProperty.Global
    public record struct PropertyKey(Guid FormatId, uint PropertyId);
    // ReSharper restore NotAccessedPositionalProperty.Global

    // PROPVARIANT, for the types used here: empty, and a string, which is a pointer in the union.
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type;
        ushort reserved1;
        ushort reserved2;
        ushort reserved3;
        public IntPtr Value;
        // The union's largest member is a pointer and a count.
        IntPtr rest;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    public interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);
}
