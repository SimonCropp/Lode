/// <summary>
/// The folders a machine on the network shares, as Explorer lists them when the machine itself
/// is opened, "\\server". Asking can take a long time when the machine is not there, so it is
/// never asked on the UI thread. WSL's machine, which won't say, shares the Linux distributions
/// installed.
/// </summary>
static partial class Shares
{
    const int success = 0;
    const int moreData = 234;
    const int shareInfoLevel = 1;
    const int preferredMaximumLength = -1;

    // A share's type is a kind, in its low bits, and flags. Disk shares are the folders; the
    // others are printers, devices and IPC$. Special shares are the administrative ones, C$
    // and ADMIN$, which Explorer never lists.
    const uint typeMask = 0xFF;
    const uint diskTree = 0;
    const uint special = 0x80000000;

    // Where WSL keeps the Linux distributions installed for the user, a key for each, named by
    // its DistributionName.
    const string distributionsKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    /// <summary>
    /// The names of the folders <paramref name="server"/> shares, in the order it gives them.
    /// Those ending in $, hidden from browsing by convention, are marked hidden.
    /// </summary>
    /// <exception cref="Win32Exception">The machine can't be asked: not there, or Lode has no
    /// rights to ask it.</exception>
    public static List<(string Name, bool Hidden)> List(string server) =>
        List(server, distributionsKey);

    /// <param name="distributions">The key under HKEY_CURRENT_USER that WSL's distributions are
    /// read from. Given in tests.</param>
    internal static List<(string Name, bool Hidden)> List(string server, string distributions)
    {
        if (IsWsl(server))
        {
            return Distributions(distributions);
        }

        var shares = new List<(string, bool)>();
        uint resume = 0;
        int result;
        do
        {
            result = NetShareEnum(server, shareInfoLevel, out var buffer, preferredMaximumLength, out var read, out _, ref resume);
            if (result != success &&
                result != moreData)
            {
                throw new Win32Exception(result);
            }

            try
            {
                var size = Marshal.SizeOf<ShareInfo1>();
                for (var index = 0; index < read; index++)
                {
                    var info = Marshal.PtrToStructure<ShareInfo1>(buffer + index * size);
                    if ((info.Type & typeMask) != diskTree ||
                        (info.Type & special) != 0 ||
                        info.Name is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    shares.Add((name, name.EndsWith('$')));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    NetApiBufferFree(buffer);
                }
            }
        }
        while (result == moreData);

        return shares;
    }

    // WSL's machine, as Windows names it now and as it was named first. It shares each Linux
    // distribution installed, but answers nobody asking what it shares, as net view
    // \\wsl.localhost says Access is denied.
    static bool IsWsl(string server) =>
        Locations.Same(server, @"\\wsl.localhost") ||
        Locations.Same(server, @"\\wsl$");

    // The distributions WSL has installed for the user, as Explorer's Linux lists them. None
    // where WSL isn't installed.
    static List<(string Name, bool Hidden)> Distributions(string keyPath)
    {
        var shares = new List<(string, bool)>();
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key == null)
        {
            return shares;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var distribution = key.OpenSubKey(name);
            if (distribution?.GetValue("DistributionName") is string {Length: > 0} distributionName)
            {
                shares.Add((distributionName, false));
            }
        }

        return shares;
    }

    /// <summary>
    /// True when <paramref name="server"/> answers with its shares.
    /// </summary>
    public static bool Answers(string server)
    {
        try
        {
            List(server);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    // SHARE_INFO_1.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShareInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Remark;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareEnum(
        string server,
        int level,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int read,
        out int total,
        ref uint resume);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);
}
