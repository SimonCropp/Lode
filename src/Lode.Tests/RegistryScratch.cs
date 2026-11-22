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

    public string AssociationsPath => AssociationsFor(ClassesPath);

    static string AssociationsFor(string classesPath) =>
        $@"{Path.GetDirectoryName(classesPath)}\Associations";

    /// <summary>
    /// For the scratch classes.
    /// </summary>
    public RepositoryLinks Links(
        string command = """
                         "C:\Tools\lode.exe"
                         """) =>
        LinksFor(ClassesPath, command);

    /// <summary>
    /// For <paramref name="classesPath"/>, a scratch's, with Windows' own choices read from beside
    /// it, and the app Windows has for links named after the app they are for, never the
    /// machine's.
    /// </summary>
    public static RepositoryLinks LinksFor(string classesPath, string command) =>
        new(
            classesPath,
            command,
            AssociationsFor(classesPath),
            (scheme, what) =>
            {
                var app = RepositoryLinks.All.Single(_ => _.Scheme == scheme).App;
                return what == NativeMethods.AssocCommand ? $"\"C:\\Apps\\{app}.exe\" \"%1\"" : app;
            });

    /// <summary>
    /// Sets a value under the classes key. A null name is the key's default value.
    /// </summary>
    public void Set(string keyPath, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{keyPath}");
        key.SetValue(name ?? "", value);
    }

    /// <summary>
    /// Deletes a key under the classes key, and everything under it.
    /// </summary>
    public void Delete(string keyPath) =>
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\{keyPath}");

    /// <summary>
    /// What a Replace by a Lode from before it added the explore verb left.
    /// </summary>
    public void OlderReplace(ExplorerReplacement replacement)
    {
        ReplaceWithoutFolderExplore(replacement);
        Delete(@"Directory\shell\explore");
        Delete(@"Drive\shell\explore");
    }

    /// <summary>
    /// What a Replace by a Lode from before it added the explore verb on Folder left: the verb
    /// on Directory and Drive only.
    /// </summary>
    public void ReplaceWithoutFolderExplore(ExplorerReplacement replacement)
    {
        replacement.Replace();
        Delete("Folder");
    }

    /// <summary>
    /// A copy of Explorer's own explore verb, as the machine's classes have it on Folder, which
    /// some tools leave in the user's classes. With a command, as a copy of Explorer's Open verb
    /// has, of the kind the machine's uses.
    /// </summary>
    public void CopyExplorersExplore(string folderClass, string? command = null)
    {
        using var explore = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{folderClass}\shell\explore");
        explore.SetValue("LaunchExplorerFlags", 24, RegistryValueKind.DWord);
        explore.SetValue("MultiSelectModel", "Document");
        explore.SetValue("ProgrammaticAccessOnly", "");
        using var commandKey = explore.CreateSubKey("command");
        commandKey.SetValue("DelegateExecute", "{11dbb47c-a525-400b-9e80-a54615a090c0}");
        if (command != null)
        {
            commandKey.SetValue("", command, RegistryValueKind.ExpandString);
        }
    }

    /// <summary>
    /// A value under the classes key, unexpanded, with its kind. A null name is the key's
    /// default value.
    /// </summary>
    public (object? Value, RegistryValueKind Kind) Raw(string keyPath, string? name)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{keyPath}")!;
        var value = key.GetValue(name ?? "", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null)
        {
            return (null, RegistryValueKind.None);
        }

        return (value, key.GetValueKind(name ?? ""));
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
