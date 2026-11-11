using Microsoft.Win32;

/// <summary>
/// A throwaway key under HKEY_CURRENT_USER that stands in for Software\Classes, so tests can
/// replace Explorer without touching the real associations.
/// </summary>
sealed class RegistryScratch : IDisposable
{
    // A key of its own directly under Software, rather than under one shared parent: tests run
    // side by side, and tidying away a shared parent that looked empty could delete it while
    // another test was creating its key inside, leaving that test reading nothing.
    readonly string path = $@"Software\Lode.Tests.{Guid.NewGuid():N}";

    public string ClassesPath => $@"{path}\Classes";

    public RegistryScratch() =>
        Registry.CurrentUser.CreateSubKey(ClassesPath).Dispose();

    public ExplorerReplacement Replacement(
        string command = """
                         "C:\Tools\lode.exe"
                         """) =>
        new(ClassesPath, command);

    /// <summary>
    /// Sets a value under the classes key. A null name is the key's default value.
    /// </summary>
    public void Set(string keyPath, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{keyPath}");
        key.SetValue(name ?? "", value);
    }

    /// <summary>
    /// Everything under the classes key. Subkey names end in a backslash; "(default)" is a
    /// key's default value.
    /// </summary>
    public Dictionary<string, object> Dump()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesPath)!;
        return Dump(classes);
    }

    static Dictionary<string, object> Dump(RegistryKey key)
    {
        var result = new Dictionary<string, object>();
        foreach (var name in key.GetValueNames())
        {
            result[name.Length == 0 ? "(default)" : name] = key.GetValue(name)!;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(name)!;
            result[$@"{name}\"] = Dump(subKey);
        }

        return result;
    }

    public void Dispose() =>
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
}
