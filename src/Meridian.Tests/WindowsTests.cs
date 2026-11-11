/// <summary>
/// Tabs moving between windows, and into windows of their own.
/// </summary>
[TUnit.Core.Executors.STAThreadExecutor]
// Shows windows, so not alongside the captures in MainFormTests.
[NotInParallel]
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

    static async Task<MainForm> Show(Fixture fixture, params string[] paths)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        var form = new QuietForm(paths, new(), places)
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
            tabs = form.Views.Select(_ => ((TabPage)_.Parent!).Text),
            active = form.ActiveView?.CurrentPath
        };

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
