[TUnit.Core.Executors.STAThreadExecutor]
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

    // Folders point at a copy of Meridian installed somewhere else. Replace stays available, to
    // point them at this copy instead.
    [Test]
    public async Task AnotherCopyIsRegistered()
    {
        using var scratch = new RegistryScratch();
        scratch.Replacement(
            """
            "D:\Old\Meridian.exe"
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
    public async Task MeridianInCharge()
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        replacement.Replace();
        using var form = Show(replacement);
        await Verify(form);
    }
#endif

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
        return form;
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
        OptionsForm(replacement, settings)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
