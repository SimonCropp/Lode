/// <summary>
/// The status bar's branch drop-up: the branches grouped and filtered, the keys, and what it
/// gives once closed. The branches are made up, so no repository is needed.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class BranchPickerTests
{
    static readonly DateTimeOffset now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    static GitBranches.Branch Branch(string name, double hoursAgo, bool remote = false, bool stashed = false) =>
        new(name, remote, now - TimeSpan.FromHours(hoursAgo), stashed);

    internal static readonly GitBranches.Listing Listing = new(
        "asyncinterfaces-pin-netfx",
        Branch("main", 15),
        [
            Branch("asyncinterfaces-pin-netfx", 5),
            Branch("DiffEngine-21.2.0", 50, stashed: true),
            Branch("Update-to-TUnit-1.86.6", 75)
        ],
        [
            Branch("cleanup", 24 * 40),
            // Long enough that the note gives way to it.
            Branch("feature/sign-in-with-the-new-provider", 24 * 6, stashed: true),
            Branch("origin/feature-on-origin", 24 * 3, remote: true)
        ]);

    // A row as the tests show it: a heading in capitals, a branch indented under it, the one
    // checked out ticked, and a message as it is.
    static IEnumerable<string> Describe(IEnumerable<BranchPicker.Row> rows) =>
        rows.Select(_ =>
        {
            if (_.Heading)
            {
                return _.Text.ToUpperInvariant();
            }

            if (_.Branch is not { } branch)
            {
                return _.Text;
            }

            var tick = "  ";
            if (!branch.Remote &&
                branch.Name == Listing.Current)
            {
                tick = "✓ ";
            }

            return $"  {tick}{branch.Name}  [{BranchPicker.Note(branch, now)}]";
        });

    [Test]
    public Task Rows() =>
        Verify(
            new
            {
                finding = Describe(BranchPicker.RowsFor(null, "")),
                all = Describe(BranchPicker.RowsFor(Listing, "")),
                // Only the groups with a match keep their headings.
                filtered = Describe(BranchPicker.RowsFor(Listing, "u")),
                twoWords = Describe(BranchPicker.RowsFor(Listing, "pin net")),
                noMatch = Describe(BranchPicker.RowsFor(Listing, "zzz")),
                none = Describe(BranchPicker.RowsFor(new(null, null, [], []), "")),
                failed = Describe(BranchPicker.RowsFor(new(null, null, [], [], "fatal: not a git repository\nmore"), ""))
            });

    [Test]
    public async Task SuggestedNameJoinsWordsWithDashes()
    {
        await Assert.That(BranchPicker.SuggestedName("  fix the  bug ")).IsEqualTo("fix-the-bug");
        await Assert.That(BranchPicker.SuggestedName("")).IsEqualTo("");
    }

    // Above what opened it, kept on the screen; below where there is no room above.
    [Test]
    public async Task Place()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var size = new Size(380, 420);

        await Assert.That(BranchPicker.Place(new(900, 1010, 150, 22), size, area)).IsEqualTo(new Point(900, 590));
        await Assert.That(BranchPicker.Place(new(1800, 1010, 100, 22), size, area)).IsEqualTo(new Point(1540, 590));
        await Assert.That(BranchPicker.Place(new(10, 100, 100, 22), size, area)).IsEqualTo(new Point(10, 122));
    }

    static async Task<BranchPicker> Open(string search = "")
    {
        var picker = new BranchPicker
        {
            Location = new(-20000, -20000),
            Search = search
        };
        picker.Show();
        await picker.Fill(Task.FromResult(Listing));
        return picker;
    }

    // Until the branches are found it says so; then the branch checked out is highlighted, as
    // GitHub Desktop has it.
    [Test]
    public async Task OpensOnTheBranchCheckedOut()
    {
        using var picker = new BranchPicker
        {
            Location = new(-20000, -20000)
        };
        picker.Show();
        await Assert.That(picker.Rows.Select(_ => _.Text)).IsEquivalentTo(["Finding branches…"]);
        await Assert.That(picker.SelectedRow).IsEqualTo(-1);

        var finding = new TaskCompletionSource<GitBranches.Listing>();
        var filling = picker.Fill(finding.Task);
        finding.SetResult(Listing);
        await filling;

        await Assert.That(picker.Rows[picker.SelectedRow].Text).IsEqualTo("asyncinterfaces-pin-netfx");
    }

    // Up and down step over the headings, and stop at the ends.
    [Test]
    public async Task ArrowsSkipHeadings()
    {
        using var picker = await Open();

        picker.Press(Keys.Up);
        await Assert.That(picker.Rows[picker.SelectedRow].Text).IsEqualTo("main");
        picker.Press(Keys.Up);
        await Assert.That(picker.Rows[picker.SelectedRow].Text).IsEqualTo("main");
        picker.Press(Keys.Down);
        await Assert.That(picker.Rows[picker.SelectedRow].Text).IsEqualTo("asyncinterfaces-pin-netfx");
        picker.Press(Keys.Down);
        picker.Press(Keys.Down);
        picker.Press(Keys.Down);
        await Assert.That(picker.Rows[picker.SelectedRow].Text).IsEqualTo("cleanup");
        picker.Press(Keys.PageDown);
        await Assert.That(picker.Rows[picker.SelectedRow].Text).IsEqualTo("origin/feature-on-origin");
    }

    [Test]
    public async Task EnterPicksTheHighlightedBranch()
    {
        using var picker = await Open("tunit");

        picker.Press(Keys.Enter);

        await Assert.That(picker.Chosen?.Name).IsEqualTo("Update-to-TUnit-1.86.6");
        await Assert.That(picker.NewBranchName).IsNull();
        await Assert.That(picker.Visible).IsFalse();
    }

    // Already there, so nothing to switch to.
    [Test]
    public async Task PickingTheBranchCheckedOutOnlyCloses()
    {
        using var picker = await Open();

        picker.Press(Keys.Enter);

        await Assert.That(picker.Chosen).IsNull();
        await Assert.That(picker.Visible).IsFalse();
    }

    // Nothing matching what was typed, Enter asks for a new branch of that name.
    [Test]
    public async Task EnterWithNoMatchAsksForANewBranch()
    {
        using var picker = await Open("fix the bug");

        picker.Press(Keys.Enter);

        await Assert.That(picker.Chosen).IsNull();
        await Assert.That(picker.NewBranchName).IsEqualTo("fix-the-bug");
    }

    [Test]
    public async Task EscapeCloses()
    {
        using var picker = await Open();

        picker.Press(Keys.Escape);

        await Assert.That(picker.Chosen).IsNull();
        await Assert.That(picker.NewBranchName).IsNull();
        await Assert.That(picker.Visible).IsFalse();
    }
}
