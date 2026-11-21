[STAThreadExecutor]
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
        form.ShowPage("File Explorer");
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
        form.ShowPage("File Explorer");
        var before = Describe(form);

        Click(form, "replaceExplorer");

        await Verify(
            new
            {
                before,
                replaced = Describe(form)
            });
    }

    // Folders point at this copy, but from a Lode that did not yet add the explore verb.
    // Replace is available again, to add it.
    [Test]
    public async Task AnOlderReplaceIsIncomplete()
    {
        using var scratch = new RegistryScratch();
        scratch.OlderReplace(scratch.Replacement());
        using var form = Show(scratch.Replacement());
        form.ShowPage("File Explorer");
        var before = Describe(form);

        Click(form, "replaceExplorer");

        await Verify(
            new
            {
                before,
                replaced = Describe(form)
            });
    }

    // Each action has a tick for the actions bar and one for the right-click menus.
    [Test]
    public async Task TickingAnActionChangesWhereItShows()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var before = new
        {
            openButton = settings.ShowsAsButton("open"),
            cutMenu = settings.ShowsInMenu("cut")
        };

        ((CheckBox) form.Controls.Find("button.open", true)[0]).Checked = true;
        ((CheckBox) form.Controls.Find("menu.cut", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = new
                {
                    openButton = settings.ShowsAsButton("open"),
                    cutMenu = settings.ShowsInMenu("cut"),
                    // Cut's button tick is untouched.
                    cutButton = settings.ShowsAsButton("cut")
                },
                changes
            });
    }

    // A Nested box nests the action under its parent there. It is greyed out while the action
    // doesn't show there, keeping what it says, and only an action with a parent has one.
    [Test]
    public async Task TickingNestedNestsTheAction()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        CheckBox Box(string name) => (CheckBox) form.Controls.Find(name, true)[0];
        var before = new
        {
            cutButtonNested = settings.NestsButton("cut"),
            terminalMenuNested = settings.NestsInMenu("terminal"),
            openButtonNestedEnabled = Box("buttonNested.open").Enabled
        };

        Box("buttonNested.cut").Checked = true;
        Box("menuNested.terminal").Checked = false;
        Box("button.cut").Checked = false;

        await Verify(
            new
            {
                before,
                after = new
                {
                    cutButtonNested = settings.NestsButton("cut"),
                    cutButtonNestedEnabled = Box("buttonNested.cut").Enabled,
                    terminalMenuNested = settings.NestsInMenu("terminal"),
                    // Terminal (admin)'s nesting is untouched.
                    terminalAdminMenuNested = settings.NestsInMenu("terminalAdmin")
                },
                newHasNestedBoxes = form.Controls.Find("buttonNested.new", true).Length > 0
            });
    }

    // Only with a code folder set. Excluding asks first, then hands over the folder and says
    // how it went; the real exclusion never runs in a test.
    [Test]
    public async Task ExcludeCodeFolderFromDefender()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        form.ShowPage("Code");
        var button = (Button) form.Controls.Find("excludeFromDefender", true)[0];
        var enabledWithoutFolder = button.Enabled;

        form.BrowseForCodeDirectory = () => @"D:\Code";
        form.ConfirmCodeDirectory = _ => true;
        ((Button) form.Controls.Find("browseCodeDirectory", true)[0]).PerformClick();
        var enabledWithFolder = button.Enabled;

        var events = new List<string>();
        var answer = false;
        form.ConfirmDefenderExclusion = folder =>
        {
            events.Add($"asked about {folder}");
            return answer;
        };
        form.ExcludeFromDefender = folder =>
        {
            events.Add($"excluded {folder}");
            return Task.FromResult(new DefenderExclusion.Outcome(true, false, [@"D:\Code\Lode", @"D:\Code\Lode\bin"], null));
        };
        form.ReportDefenderExclusion = (folder, outcome) =>
            events.Add($"reported {folder}: removed {string.Join(", ", outcome.Removed)}");

        // Turned down: nothing is excluded.
        button.PerformClick();
        Application.DoEvents();
        answer = true;
        button.PerformClick();
        Application.DoEvents();

        await Verify(
            new
            {
                enabledWithoutFolder,
                enabledWithFolder,
                events
            });
    }

    [Test]
    public async Task UntickingShowRecentChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
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

    // On by default, unlike Explorer's Expand to open folder.
    [Test]
    public async Task UntickingExpandToOpenFolderChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
        var before = settings.ExpandToOpenFolder;

        ((CheckBox) form.Controls.Find("expandToOpenFolder", true)[0]).Checked = false;

        await Verify(
            new
            {
                before,
                after = settings.ExpandToOpenFolder,
                changes
            });
    }

    // Off by default, so purged files go to the Recycle Bin.
    [Test]
    public async Task TickingPurgePermanentlyChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var before = settings.PurgePermanently;

        ((CheckBox) form.Controls.Find("purgePermanently", true)[0]).Checked = true;

        await Verify(
            new
            {
                before,
                after = settings.PurgePermanently
            });
    }

    // Off by default, with the hours greyed out until it is ticked.
    [Test]
    public async Task TickingCloseUnusedTabsChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var tick = (CheckBox) form.Controls.Find("closeUnusedTabs", true)[0];
        var hours = (NumericUpDown) form.Controls.Find("unusedTabHours", true)[0];
        var before = new
        {
            settings.CloseUnusedTabs,
            settings.UnusedTabHours,
            hoursEnabled = hours.Enabled
        };

        tick.Checked = true;
        hours.Value = 8;

        await Verify(
            new
            {
                before,
                after = new
                {
                    settings.CloseUnusedTabs,
                    settings.UnusedTabHours,
                    hoursEnabled = hours.Enabled
                }
            });
    }

    // Same as Windows by default. Picking another is kept for the next start, which is when it
    // takes effect.
    [Test]
    public async Task PickingAThemeChangesTheSetting()
    {
        using var scratch = new RegistryScratch();
        var settings = new Settings();
        using var form = Show(scratch.Replacement(), settings);
        var theme = (ComboBox) form.Controls.Find("theme", true)[0];
        var before = new
        {
            settings.Theme,
            shown = theme.SelectedItem
        };

        theme.SelectedIndex = (int) Theme.Dark;

        await Verify(
            new
            {
                before,
                after = settings.Theme
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
        var changes = new List<SettingsChange>();
        form.SettingsChanged += changes.Add;
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
    // so, like MainFormTests, these only run in Debug builds. A capture of each page; File
    // Explorer's in both states.
    [Test]
    public Task GeneralPage() =>
        VerifyPage(
            "General",
            new()
            {
                DefaultDirectory = @"D:\Documents",
                TextEditor = editors[0].Path
            });

    [Test]
    public Task ViewPage() =>
        VerifyPage("View", new());

    [Test]
    public Task ActionsPage() =>
        VerifyPage("Actions", new());

    [Test]
    public Task CodePage() =>
        VerifyPage("Code", new() {CodeDirectory = @"C:\Code"});

    [Test]
    public Task ExplorerInCharge() =>
        VerifyPage("File Explorer", new());

    [Test]
    public Task LodeInCharge() =>
        VerifyPage("File Explorer", new(), replace: true);

    [Test]
    public Task UpdatesPage() =>
        VerifyPage(
            "Updates",
            new(),
            updates: TestUpdates.Installed(
                new()
                {
                    AvailableUpdate = "0.4.0"
                }));

    static async Task VerifyPage(string page, Settings settings, bool replace = false, Updates? updates = null)
    {
        using var scratch = new RegistryScratch();
        var replacement = scratch.Replacement();
        if (replace)
        {
            replacement.Replace();
        }

        using var form = Show(replacement, settings, updates);
        form.ShowPage(page);
        form.Refresh();
        Application.DoEvents();
        await Verify(form);
    }
#endif

    // Picking a page in the list shows it and hides the rest; Ctrl+Tab and Ctrl+Shift+Tab go
    // round them.
    [Test]
    public async Task PickingAPageShowsIt()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        var list = (PageList) form.Controls.Find("pages", true)[0];
        var opened = Shown(form);

        list.SelectedIndex = 4;
        var picked = Shown(form);
        form.Press(Keys.Control | Keys.Tab);
        var next = Shown(form);
        form.Press(Keys.Control | Keys.Tab);
        var wrapped = Shown(form);
        form.Press(Keys.Control | Keys.Shift | Keys.Tab);

        await Verify(
            new
            {
                pages = Enumerable.Range(0, list.Count).Select(list.PageText),
                opened,
                picked,
                next,
                wrapped,
                back = Shown(form)
            });
    }

    // What shows: the code folder box is on the Code page, the tick for hidden items on View.
    static string Shown(Form form)
    {
        if (form.Controls.Find("updateStatus", true)[0].Visible)
        {
            return "Updates";
        }

        if (form.Controls.Find("codeDirectory", true)[0].Visible)
        {
            return "Code";
        }

        if (form.Controls.Find("explorerStatus", true)[0].Visible)
        {
            return "File Explorer";
        }

        if (form.Controls.Find("showHidden", true)[0].Visible)
        {
            return "View";
        }

        if (form.Controls.Find("button.cut", true)[0].Visible)
        {
            return "Actions";
        }

        if (form.Controls.Find("textEditor", true)[0].Visible)
        {
            return "General";
        }

        return "none";
    }

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
        var settings = new Settings {TextEditor = editors[1].Path};
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
        form.ShowPage("Code");
        form.BrowseForCodeDirectory = () => @"C:\Code";

        // Asked first, as Lode watches the folder: not used when the answer is no.
        var asked = new List<string>();
        var answer = false;
        form.ConfirmCodeDirectory = folder =>
        {
            asked.Add(folder);
            return answer;
        };
        Click(form, "browseCodeDirectory");
        var declined = settings.CodeDirectory;
        answer = true;
        Click(form, "browseCodeDirectory");

        // The one it already is isn't asked about again.
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
                asked,
                declined,
                chosen,
                cleared = new
                {
                    setting = settings.CodeDirectory,
                    clearEnabled = form.Controls.Find("clearCodeDirectory", true)[0].Enabled
                }
            });
    }

    // What the Updates page says, as things stand.
    [Test]
    public async Task UpdateStatus()
    {
        var failed = TestUpdates.Installed(
            new(),
            _ => throw new HttpRequestException("No such host is known. (api.nuget.org:443)"));
        await failed.Check();

        await Verify(
            new
            {
                build = OptionsForm.UpdateStatus(TestUpdates.Build()),
                notCheckedYet = OptionsForm.UpdateStatus(TestUpdates.Installed(new())),
                upToDate = OptionsForm.UpdateStatus(TestUpdates.Installed(new() {LastUpdateCheck = TestUpdates.Now.AddHours(-3)})),
                available = OptionsForm.UpdateStatus(TestUpdates.Installed(new() {AvailableUpdate = "0.4.0"})),
                failed = OptionsForm.UpdateStatus(failed)
            });
    }

    // Check now asks straight away, and nothing can be clicked again until NuGet answers.
    [Test]
    public async Task CheckingNow()
    {
        using var scratch = new RegistryScratch();
        var answer = new TaskCompletionSource<NuGetVersion?>();
        var updates = TestUpdates.Installed(new(), _ => answer.Task);
        using var form = Show(scratch.Replacement(), updates: updates);
        form.ShowPage("Updates");
        var before = DescribeUpdates(form);

        Click(form, "checkForUpdates");
        var checking = DescribeUpdates(form);
        answer.SetResult(NuGetVersion.Parse("0.4.0"));
        await Pump.Until(() => !updates.Checking);
        Application.DoEvents();

        await Verify(
            new
            {
                before,
                checking,
                answered = DescribeUpdates(form)
            });
    }

    // Update starts the update and closes Options, for the window to exit Lode, which the update
    // waits for.
    [Test]
    public async Task UpdatingClosesOptions()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(
            scratch.Replacement(),
            updates: TestUpdates.Installed(
                new()
                {
                    AvailableUpdate = "0.4.0"
                }));
        form.ShowPage("Updates");
        var before = DescribeUpdates(form);

        Click(form, "applyUpdate");

        await Verify(
            new
            {
                before,
                form.UpdatesStarted,
                form.UpdateStarted,
                closed = !form.Visible
            });
    }

    // Updating has Lode exit, which would stop an action running part way, so it waits for the
    // actions to finish.
    [Test]
    public async Task UpdateWaitsForActionsRunning()
    {
        using var scratch = new RegistryScratch();
        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        try
        {
            var running = ActionLog.Run("Git pull", @"C:\Code\Lode", () => finish.Task);
            using var form = Show(
                scratch.Replacement(),
                updates: TestUpdates.Installed(
                    new()
                    {
                        AvailableUpdate = "0.4.0"
                    }));
            form.ShowPage("Updates");
            var whileRunning = DescribeUpdates(form);

            finish.SetResult(new(true, "Done", ""));
            await running;
            Application.DoEvents();

            await Verify(
                new
                {
                    whileRunning,
                    finished = DescribeUpdates(form)
                });
        }
        finally
        {
            ActionLog.Reset();
        }
    }

    // On a screen too short for it, a small one or one scaled up, the dialog is no taller than
    // the screen and the pages scroll, so Close is still on it. Each page shows from its top.
    [Test]
    public async Task FitsWithinAShortScreen()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(scratch.Replacement());
        form.ShowPage("Actions");
        var fullHeight = form.Height;
        var area = new Rectangle(form.Left, form.Top, 2000, fullHeight - 150);

        form.FitWithin(area);
        Application.DoEvents();
        var close = form.AcceptButton as Control;

        await Assert.That(form.Height).IsLessThanOrEqualTo(area.Height);
        await Assert.That(form.PagesScroll).IsTrue();
        await Assert.That(close!.Visible).IsTrue();
        await Assert.That(form.RectangleToClient(close.RectangleToScreen(close.ClientRectangle)).Bottom).IsLessThanOrEqualTo(form.ClientSize.Height);
    }

    // A build has nothing to update, and doesn't ask.
    [Test]
    public async Task ABuildHasNothingToUpdate()
    {
        using var scratch = new RegistryScratch();
        using var form = Show(
            scratch.Replacement(),
            updates: TestUpdates.Build(
                new()
                {
                    AvailableUpdate = "0.4.0"
                }));
        form.ShowPage("Updates");

        await Verify(DescribeUpdates(form));
    }

    static object DescribeUpdates(Form form) =>
        new
        {
            status = form.Controls.Find("updateStatus", true)[0].Text,
            updateEnabled = form.Controls.Find("applyUpdate", true)[0].Enabled,
            checkEnabled = form.Controls.Find("checkForUpdates", true)[0].Enabled
        };

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
    static QuietForm Show(ExplorerReplacement replacement, Settings? settings = null, Updates? updates = null)
    {
        var form = new QuietForm(replacement, settings ?? new(), updates ?? TestUpdates.Build())
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

    sealed class QuietForm : OptionsForm
    {
        public QuietForm(ExplorerReplacement replacement, Settings settings, Updates updates) :
            base(replacement, settings, editors, updates) =>
            // Never the real update, which would update Lode once the tests end.
            StartUpdate = () => UpdatesStarted++;

        public int UpdatesStarted { get; private set; }

        protected override bool ShowWithoutActivation => true;

        // As the keys would: the modifiers of a real key press come from the keyboard.
        public void Press(Keys keys)
        {
            var message = new Message();
            ProcessCmdKey(ref message, keys);
            Application.DoEvents();
        }
    }
}
