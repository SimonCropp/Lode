[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class TaskbarIdentityTests
{
    // What a pin made from the window gets: Lode, started the way Replace starts it, with
    // Windows' folder icon, the one the window shows while it runs.
    [Test]
    public async Task WindowSaysHowToPinLode()
    {
        using var fixture = Fixture.Create();
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "120 GB free of 500 GB");
        using var form = new QuietForm([fixture.Root], new(), places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);

        var identity = TaskbarIdentity.Read(form.Handle);

        await Assert.That(identity).IsEqualTo(
            new(
                "Lode",
                ExplorerReplacement.PreferredCommand(),
                "Lode",
                TaskbarIdentity.FolderIconLocation()));
    }

    [Test]
    public async Task ClearTakesThePropertiesOff()
    {
        using var form = new Form();
        TaskbarIdentity.Apply(form.Handle, "\"lode.exe\"", "imageres.dll,-3");
        var applied = TaskbarIdentity.Read(form.Handle);

        TaskbarIdentity.Clear(form.Handle);

        await Verify(
            new
            {
                applied,
                cleared = TaskbarIdentity.Read(form.Handle)
            })
            .Snapshot(
                """
                {
                  applied: {
                    Id: Lode,
                    Command: "lode.exe",
                    Name: Lode,
                    Icon: imageres.dll,-3
                  },
                  cleared: {}
                }
                """);
    }

    [Test]
    public async Task FolderIconIsInAFileOnThisMachine()
    {
        var location = TaskbarIdentity.FolderIconLocation();
        await Assert.That(location).IsNotNull();

        var separator = location!.LastIndexOf(',');
        await Assert.That(File.Exists(location[..separator])).IsTrue();
        await Assert.That(int.TryParse(location[(separator + 1)..], CultureInfo.InvariantCulture, out _)).IsTrue();
    }

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }
}
