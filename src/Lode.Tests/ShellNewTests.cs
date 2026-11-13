/// <summary>
/// Reading the New types from a stand-in for HKEY_CLASSES_ROOT.
/// </summary>
public class ShellNewTests
{
    [Test]
    public async Task Read()
    {
        using var scratch = new RegistryScratch();
        // On the extension itself.
        scratch.Set(@".empty\ShellNew", "NullFile", "");
        // Under the extension's ProgID.
        scratch.Set(".doc", null, "Lode.Doc");
        scratch.Set(@".doc\Lode.Doc\ShellNew", "Data", "content");
        scratch.Set(@".tmpl\ShellNew", "FileName", "template.tmpl");
        // Under some other subkey: Explorer ignores it.
        scratch.Set(@".stale\Other\ShellNew", "NullFile", "");
        // A command Lode does not run.
        scratch.Set(@".cmd\ShellNew", "Command", "notepad.exe");
        // A shortcut, made by a wizard.
        scratch.Set(@".lnk\ShellNew", "NullFile", "");
        // Not an extension.
        scratch.Set(@"txtfile\ShellNew", "NullFile", "");

        using var classes = Registry.CurrentUser.OpenSubKey(scratch.ClassesPath)!;
        var entries = ShellNew.Read(classes, _ => $"Type{_}");

        await Verify(
            entries.Select(_ => new
            {
                _.Extension,
                _.TypeName,
                Data = _.Data == null ? null : Encoding.UTF8.GetString(_.Data),
                _.Template
            }));
    }
}
