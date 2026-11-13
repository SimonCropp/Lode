/// <summary>
/// Tabs moving between windows, and into windows of their own.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
// Shows windows, so not alongside the captures in MainFormTests.
public class WindowsTests
{
    [Test]
    public async Task MoveTabToAnotherWindow()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root);
        var moved = source.Views.Last();

        source.MoveTab((TabPage)moved.Parent!, target);

        // Still tied to the window it came from, this would retitle that one instead.
        moved.Navigate(fixture.At("Source", "Deep"));
        await Pump.Loaded(moved);

        await Verify(
            new
            {
                source = Describe(source),
                target = Describe(target)
            });
    }

    [Test]
    public async Task MovingTheLastTabClosesItsWindow()
    {
        using var fixture = Fixture.Create();
        var source = await Show(fixture, fixture.At("Docs"));
        using var target = await Show(fixture, fixture.Root);

        source.MoveTab((TabPage)source.Views.Single().Parent!, target);
        Application.DoEvents();

        await Verify(
            new
            {
                sourceClosed = source.IsDisposed,
                target = Describe(target)
            });
    }

    [Test]
    public async Task MoveTabToNewWindow()
    {
        using var fixture = Fixture.Create();
        using var source = await Show(fixture, fixture.At("Source"), fixture.At("Docs"));
        var opened = new List<MainForm>();
        MainForm.WindowOpened += opened.Add;
        try
        {
            using var window = source.MoveTabToNewWindow((TabPage)source.Views.Last().Parent!, new(-20000, -20000));
            Application.DoEvents();

            await Verify(
                new
                {
                    source = Describe(source),
                    window = Describe(window),
                    announced = opened.SequenceEqual([window])
                });
        }
        finally
        {
            MainForm.WindowOpened -= opened.Add;
        }
    }

    [Test]
    public async Task CloseTabsToTheLeftAndRight()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"), fixture.At("Source"), fixture.At("Archive"), fixture.At("Source", "Deep"));
        var middle = (TabPage)form.Views.ElementAt(2).Parent!;

        form.CloseTabsToTheRight(middle);
        var afterRight = Describe(form);
        form.CloseTabsToTheLeft(middle);

        await Verify(
            new
            {
                afterRight,
                afterLeft = Describe(form)
            });
    }

    // Most recently closed first, each at the folder it was showing.
    [Test]
    public async Task ReopenClosedTabs()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"), fixture.At("Source"));
        form.CloseOtherTabs((TabPage)form.Views.First().Parent!);
        var afterClose = Describe(form);

        form.ReopenClosedTab();
        await Pump.Loaded(form.ActiveView!);
        var reopenedOnce = Describe(form);
        form.ReopenClosedTab();
        await Pump.Loaded(form.ActiveView!);

        await Verify(
            new
            {
                afterClose,
                reopenedOnce,
                reopenedTwice = Describe(form),
                nothingLeft = !form.CanReopenClosedTab
            });
    }

    // Which of the actions bar's buttons work with nothing selected, then with a file.
    [Test]
    public async Task ActionsBar()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        var nothingSelected = form.DescribeActions().ToList();
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);
        form.UpdateActions();

        await Verify(
            new
            {
                nothingSelected,
                fileSelected = form.DescribeActions().ToList()
            });
    }

    // On a display above 100% the form scales its controls after building them. The boxes in
    // the address bar and filter frames must still sit centred inside them, not pushed down and
    // over the right border.
    [Test]
    public async Task BoxesStayInsideTheirFramesWhenScaled()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.Scale(new SizeF(1.25f, 1.25f));
        var address = (TextBox)form.Controls.Find("address", true)[0];
        ((Breadcrumb)address.Parent!).BeginEdit();
        Application.DoEvents();

        await Verify(
            new
            {
                filter = Placement((TextBox)form.Controls.Find("filter", true)[0]),
                address = Placement(address)
            });
    }

    static object Placement(TextBox box)
    {
        var frame = box.Parent!.ClientSize;
        return new
        {
            insideFrame = box.Left >= 0 && box.Right <= frame.Width,
            gapAbove = box.Top,
            gapBelow = frame.Height - box.Bottom
        };
    }

    // The bar shows the buttons Options asks for, in the catalog's order, and each does what its
    // right-click menu entry would.
    [Test]
    public async Task ActionsBarFollowsOptions()
    {
        using var fixture = Fixture.Create();
        var settings = new Settings();
        settings.SetPlacement("openInNewTab", button: true, menu: true);
        settings.SetPlacement("cut", button: false, menu: true);
        settings.SetPlacement(LodeActions.More, button: true, menu: true);
        using var form = await Show(fixture, settings, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("Docs"));
        await Pump.Loaded(form.ActiveView);
        form.UpdateActions();
        var buttons = form.DescribeActions().ToList();

        form.RunAction("openInNewTab");
        await Pump.Loaded(form.Views.Last());

        await Verify(
            new
            {
                buttons,
                tabs = form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList()
            });
    }

    // See more, at the end of the bar, lists the actions that are not buttons, greyed where
    // they do not apply: here, to a file.
    [Test]
    public async Task SeeMoreListsTheOtherActions()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);
        form.ActiveView!.Reveal(fixture.At("notes.aleph"));
        await Pump.Loaded(form.ActiveView);

        await Verify(form.OverflowCommands(Point.Empty).Select(DescribeCommand));
    }

    // The ⋯ menu at the right of the address bar: Lode's own commands, none of the actions.
    [Test]
    public async Task WindowMenu()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root);

        await Verify(form.WindowCommands().Select(DescribeCommand));
    }

    static string DescribeCommand(QuickMenu.Command? command)
    {
        if (command == null)
        {
            return "----";
        }

        if (command.Enabled)
        {
            return command.Text;
        }

        return $"{command.Text} (disabled)";
    }

    // What Ctrl+Shift+P lists with nothing selected.
    [Test]
    public async Task PaletteCommands()
    {
        using var fixture = Fixture.Create();
        using var form = await Show(fixture, fixture.Root, fixture.At("Docs"));

        // New's file types depend on what is installed.
        var commands = form.PaletteCommands()
            .Where(_ => !_.Name.StartsWith("New: ") || _.Name == "New: Folder")
            .Select(_ => _.Shortcut.Length == 0 ? _.Name : $"{_.Name} ({_.Shortcut})");
        await Verify(commands);
    }

    static Task<MainForm> Show(Fixture fixture, params string[] paths) =>
        Show(fixture, new(), paths);

    static async Task<MainForm> Show(Fixture fixture, Settings settings, params string[] paths)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm(paths, settings, places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        foreach (var view in form.Views)
        {
            await Pump.Loaded(view);
        }

        return form;
    }

    static object Describe(MainForm form) =>
        new
        {
            title = form.Text,
            tabs = form.Views.Select(_ => ((TabPage)_.Parent!).Text).ToList(),
            active = form.ActiveView?.CurrentPath
        };

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
