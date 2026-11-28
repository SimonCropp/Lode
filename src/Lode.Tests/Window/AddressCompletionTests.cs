/// <summary>
/// What the address bar suggests as a path is typed into it: the folders of the folder the text
/// is in, taken from the folder open, and the shell's names.
/// </summary>
public class AddressCompletionTests
{
    // Text that isn't a full path is taken from the folder open, as Enter takes it: the folders
    // in the folder the text is in, as the text names them, and not its files.
    [Test]
    public async Task TextIsTakenFromTheFolderOpen()
    {
        using var fixture = Scratch();

        await Assert.That(Offered(@"src\Lo", fixture.Root)).IsEquivalentTo([@"src\Lode"], CollectionOrdering.Matching);
        await Assert.That(Offered(@"src\", fixture.Root)).IsEquivalentTo([@"src\Lib", @"src\Lode"], CollectionOrdering.Matching);
        await Assert.That(Offered("src/L", fixture.Root)).IsEquivalentTo(["src/Lib", "src/Lode"], CollectionOrdering.Matching);
        await Assert.That(Offered("S", fixture.Root)).IsEquivalentTo(["Source", "src"], CollectionOrdering.Matching);
    }

    // ..\ offers the folders beside the folder open, and .. itself is offered as a dot is typed.
    [Test]
    public async Task DotDotOffersTheFoldersOfTheFolderAbove()
    {
        using var fixture = Scratch();
        var source = fixture.At("Source");

        await Assert.That(Offered(@"..\", source)).IsEquivalentTo(
            [@"..\Archive", @"..\Cache", @"..\Docs", @"..\Source", @"..\src"],
            CollectionOrdering.Matching);
        await Assert.That(Offered(".", source)).IsEquivalentTo([".."], CollectionOrdering.Matching);
    }

    // A full path offers the folders of the folder it names, wherever the folder open is.
    [Test]
    public async Task AFullPathOffersTheFoldersOfTheFolderItNames()
    {
        using var fixture = Scratch();
        var typed = fixture.At("Do");

        await Assert.That(Offered(typed, fixture.At("Source", "Deep"))).IsEquivalentTo([fixture.At("Docs")], CollectionOrdering.Matching);
        await Assert.That(Offered(typed, Locations.ThisPC)).IsEquivalentTo([fixture.At("Docs")], CollectionOrdering.Matching);
    }

    // Hidden folders are offered only while hidden items show, as the list shows them.
    [Test]
    public async Task HiddenFoldersAsTheOptionsShowThem()
    {
        using var fixture = Scratch();

        await Assert.That(Offered("Ca", fixture.Root, showHidden: true)).IsEquivalentTo(["Cache"], CollectionOrdering.Matching);
        await Assert.That(Offered("Ca", fixture.Root, showHidden: false)).IsEmpty();
    }

    // Once the text starts with shell:, the shell's names for its folders, as Windows knows them:
    // Documents is shell:Personal, and shell:DocumentsLibrary its library. Before, a name that
    // only starts as shell: does is offered none of them.
    [Test]
    public async Task ShellOffersTheShellsNames()
    {
        using var fixture = Scratch();

        var offered = Offered("shell:Do", fixture.Root);

        await Assert.That(offered).Contains("shell:Downloads");
        await Assert.That(offered).Contains("shell:DocumentsLibrary");
        await Assert.That(offered.All(_ => _.StartsWith("shell:Do", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(Offered("SHELL:app", fixture.Root)).Contains("shell:AppData");
        await Assert.That(Offered("sh", fixture.Root)).IsEmpty();
    }

    // Nothing for a folder that isn't there, and nothing taken from This PC, which a path not
    // given in full would be taken from Lode's own folder at; This PC's own are its drives.
    [Test]
    public async Task NothingWhereThereIsNothingToGoTo()
    {
        using var fixture = Scratch();
        var drive = Path.GetPathRoot(fixture.Root)!;

        await Assert.That(Offered(@"Gone\", fixture.Root)).IsEmpty();
        await Assert.That(Offered(@"Windows\", Locations.ThisPC)).IsEmpty();
        await Assert.That(Offered(drive[..1], Locations.ThisPC)).Contains(drive);
    }

    // Typed into the address, Windows asks for the folders on a thread of its own, so a share or
    // a drive slow to answer holds up only the list: the typing goes on while the folders are
    // read, and once they are, the list offers them, the first picked with the down arrow.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task TheFoldersAreReadOffTheUIThreadAsTheAddressIsTyped()
    {
        using var fixture = Scratch();
        using var form = await Show(fixture);
        var completion = form.AddressBar.Completion;
        using var gate = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var readOn = new List<int>();
        completion.ReadFolders = (location, showHidden, hiddenSystem) =>
        {
            lock (readOn)
            {
                readOn.Add(Environment.CurrentManagedThreadId);
            }

            // Held on the UI thread, the typing would not come back until the wait gave up.
            gate.Wait(TimeSpan.FromSeconds(20));
            return Breadcrumb.FoldersIn(location, showHidden, hiddenSystem);
        };
        var box = (TextBox) form.Controls.Find("address", true)[0];
        Edit(form);

        Type(box, "s");
        await Pump.Until(() => Read(readOn).Count > 0);

        // On to the shell's names and back, which has Windows start the list again, while a
        // read holds it up, then on down the path.
        Type(box, "hell:Do");
        var shellWhileReading = box.Text;
        Type(box, "\b\b\b\b\b\b\b" + @"rc\Lo");
        var whileReading = box.Text;
        gate.Set();
        await Pump.Until(() => completion.Suggesting);
        await Pump.Until(() => Read(readOn).Count > 1);
        Press(box, Keys.Down);

        await Assert.That(shellWhileReading).IsEqualTo("shell:Do");
        await Assert.That(whileReading).IsEqualTo(@"src\Lo");
        await Assert.That(Read(readOn)).DoesNotContain(uiThread);
        await Assert.That(box.Text).IsEqualTo(@"src\Lode");
    }

    // Typed again once another folder is open, the address offers that folder's folders, not
    // those of the folder open when it was last typed in.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task EachTimeTheAddressIsTypedItIsFromTheFolderOpenThen()
    {
        using var fixture = Scratch();
        using var form = await Show(fixture);
        var completion = form.AddressBar.Completion;
        var box = (TextBox) form.Controls.Find("address", true)[0];

        Edit(form);
        Type(box, "So");
        await Pump.Until(() => completion.Suggesting);
        Press(box, Keys.Down);
        var first = box.Text;
        Press(box, Keys.Enter);
        await Pump.Loaded(form.ActiveView!);
        Edit(form);
        Type(box, "D");
        await Pump.Until(() => completion.Suggesting);
        Press(box, Keys.Down);

        await Assert.That(first).IsEqualTo("Source");
        await Assert.That(form.ActiveView!.CurrentPath).IsEqualTo(fixture.At("Source"));
        await Assert.That(box.Text).IsEqualTo("Deep");
    }

    // Typed on from the folders' s to shell:, the address offers the shell's names: Windows
    // works out what to suggest only as a separator is typed, which shell: has none of.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task TheShellsNamesAreOfferedOnceShellIsTyped()
    {
        using var fixture = Scratch();
        using var form = await Show(fixture);
        var completion = form.AddressBar.Completion;
        var box = (TextBox) form.Controls.Find("address", true)[0];

        Edit(form);
        Type(box, "shell:Downl");
        await Pump.Until(() => completion.Suggesting);
        Press(box, Keys.Down);

        await Assert.That(box.Text).IsEqualTo("shell:Downloads");
    }

    static async Task<QuietForm> Show(Fixture fixture)
    {
        var places = new Places([fixture.Root], [], ShowComputer: false, _ => "");
        var form = new QuietForm([fixture.Root], new(), places)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        form.Show();
        await Pump.Loaded(form.ActiveView!);
        return form;
    }

    // Ctrl+L, and the path the box starts with selected, as it is once the box has the focus, so
    // what is typed replaces it.
    static void Edit(MainForm form)
    {
        form.AddressBar.FocusAddress();
        Application.DoEvents();
    }

    static List<int> Read(List<int> readOn)
    {
        lock (readOn)
        {
            return readOn.ToList();
        }
    }

    // Each sent as typing it sends it, through Windows' list, which watches what is typed.
    static void Type(TextBox box, string text)
    {
        const int character = 0x0102;
        foreach (var letter in text)
        {
            SendMessage(box.Handle, character, letter, 1);
        }
    }

    static void Press(TextBox box, Keys key)
    {
        const int keyDown = 0x0100;
        const int keyUp = 0x0101;
        SendMessage(box.Handle, keyDown, (int) key, 1);
        SendMessage(box.Handle, keyUp, (int) key, 1);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    sealed class QuietForm(string[] arguments, Settings settings, Places places) :
        MainForm(arguments, settings, places)
    {
        protected override bool ShowWithoutActivation => true;
    }

    // What Windows' list offers for the text: what is suggested for the folder the text is in,
    // the text up to its last separator, narrowed to what starts with the text.
    static List<string> Offered(string typed, string folder, bool showHidden = true)
    {
        var separator = typed.LastIndexOfAny(['\\', '/']);
        string? expanded = null;
        if (separator >= 0)
        {
            expanded = typed[..(separator + 1)];
        }

        return AddressCompletion.Suggestions(typed, expanded, folder, showHidden, null, Breadcrumb.FoldersIn)
            .Where(_ => _.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // The fixture, with a src folder of folders and a file beside them.
    static Fixture Scratch([CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var fixture = Fixture.Create(file, member);
        Directory.CreateDirectory(fixture.At("src", "Lode"));
        Directory.CreateDirectory(fixture.At("src", "Lib"));
        File.WriteAllText(fixture.At("src", "Lonely.txt"), "");
        return fixture;
    }
}
