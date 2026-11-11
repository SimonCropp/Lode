[TUnit.Core.Executors.STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class OptionsFormTests
{
    [Test]
    public async Task ReplaceThenRestore()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        var before = Describe(form);

        Click(form, "replaceExplorer");
        var replaced = Describe(form);
        Click(form, "restoreExplorer");

        await Verify(
            new
            {
                before,
                replaced,
                restored = Describe(form),
                registry = scratch.Dump()
            });
    }

    // Folders point at a copy of Lode installed somewhere else. Replace stays available, to
    // point them at this copy instead.
    [Test]
    public async Task AnotherCopyIsRegistered()
    {
        using var scratch = new RegistryScratch();
        scratch.Replacement(
            """
            "D:\Old\Lode.exe"
            """).Replace();
        using var form = Show(scratch.Replacement());
        var before = Describe(form);

        Click(form, "replaceExplorer");

        await Verify(
            new
            {
                before,
                replaced = Describe(form)
            });
    }

    [Test]
    public async Task UntickingShowRecentChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = 0;
        form.SettingsChanged += () => changes++;
        var before = settings.ShowRecent;

        ((CheckBox) form.Controls.Find("showRecent", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = settings.ShowRecent,
                changes
            });
    }

    // Saved as the dialog closes, not only when the last window does.
    [Test]
    public async Task ClosingSavesTheSettings()
    {
        using var scratch = new RegistryScratch();
        var path = Path.Combine(Path.GetTempPath(), "LodeTests", $"settings.{Guid.NewGuid():N}.json");
        try
        {
            var settings = Settings.Load(path);
            using var form = Show(scratch.Replacement(), settings);
            ((CheckBox) form.Controls.Find("showHidden", true)[0]).Checked = false;
            var savedWhileOpen = File.Exists(path);

            form.Close();

            await Verify(
                new
                {
                    savedWhileOpen,
                    reloaded = Settings.Load(path).ShowHidden
                });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task UntickingShowHiddenChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = 0;
        form.SettingsChanged += () => changes++;
        var before = settings.ShowHidden;

        ((CheckBox) form.Controls.Find("showHidden", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = settings.ShowHidden,
                changes
            });
    }

#if DEBUG
    // Pixels depend on the Windows version, theme and fonts of the machine that captured them,
    // so, like MainFormTests, these only run in Debug builds.
    [Test]
    public async Task ExplorerInCharge()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        await Verify(form);
    }

    [Test]
    public async Task LodeInCharge()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        replacement.Replace();
        using var form = Show(replacement);
        await Verify(form);
    }
#endif

    [Test]
    public async Task ChoosingAnEditorChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var before = Editors(form);

        EditorBox(form).SelectedIndex = 1;

        await Verify(
            new
            {
                before,
                setting = settings.TextEditor
            });
    }

    [Test]
    public async Task BrowsingAddsTheEditor()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        form.BrowseForEditor = () => @"C:\Tools\MyEditor.exe";

        SelectBrowse(form);

        await Verify(
            new
            {
                after = Editors(form),
                setting = settings.TextEditor
            });
    }

    [Test]
    public async Task CancellingBrowseKeepsTheEditor()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings { TextEditor = editors[1].Path };
        using var form = Show(scratch.Replacement(), settings);
        form.BrowseForEditor = () => null;

        SelectBrowse(form);

        await Verify(
            new
            {
                after = Editors(form),
                setting = settings.TextEditor
            });
    }

    [Test]
    public async Task ChoosingAndClearingTheCodeFolder()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        form.BrowseForCodeDirectory = () => @"C:\Code";

        Click(form, "browseCodeDirectory");
        var chosen = new
        {
            setting = settings.CodeDirectory,
            shown = form.Controls.Find("codeDirectory", true)[0].Text,
            clearEnabled = form.Controls.Find("clearCodeDirectory", true)[0].Enabled
        };
        Click(form, "clearCodeDirectory");

        await Verify(
            new
            {
                chosen,
                cleared = new
                {
                    setting = settings.CodeDirectory,
                    clearEnabled = form.Controls.Find("clearCodeDirectory", true)[0].Enabled
                }
            });
    }

    static readonly TextEditors.Editor[] editors =
    [
        new("Visual Studio Code", @"C:\Programs\VS Code\Code.exe"),
        new("Notepad", @"C:\Windows\notepad.exe")
    ];

    static ComboBox EditorBox(Form form) =>
        (ComboBox) form.Controls.Find("textEditor", true)[0];

    static void SelectBrowse(Form form)
    {
        var box = EditorBox(form);
        box.SelectedIndex = box.Items.Count - 1;
    }

    // The list as it reads, the selected entry marked.
    static List<string> Editors(Form form)
    {
        var box = EditorBox(form);
        return box.Items
            .Cast<object>()
            .Select((item, index) => index == box.SelectedIndex ? $"{item}   <- selected" : item.ToString()!)
            .ToList();
    }

    // Shown, off screen and without taking the focus: buttons only click, and forms only draw,
    // once shown.
    static QuietForm Show(ExplorerReplacement replacement, Settings? settings = null)
    {
        var form = new QuietForm(replacement, settings ?? new())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        Application.DoEvents();

        // One pass of messages doesn't always get every nested panel created and laid out, and a
        // capture then misses whole rows. Make sure all of it exists, then lay out and draw.
        CreateHandles(form);
        form.PerformLayout();
        form.Refresh();
        Application.DoEvents();
        return form;
    }

    static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
        {
            CreateHandles(child);
        }
    }

    static void Click(Form form, string name)
    {
        ((Button) form.Controls.Find(name, true)[0]).PerformClick();
        Application.DoEvents();
    }

    static object Describe(Form form) =>
        new
        {
            status = form.Controls.Find("explorerStatus", true)[0].Text,
            replaceEnabled = form.Controls.Find("replaceExplorer", true)[0].Enabled,
            restoreEnabled = form.Controls.Find("restoreExplorer", true)[0].Enabled
        };

    sealed class QuietForm(ExplorerReplacement replacement, Settings settings) :
        OptionsForm(replacement, settings, editors)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
