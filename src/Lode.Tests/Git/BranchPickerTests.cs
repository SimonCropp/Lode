/// <summary>
/// The status bar's branch drop-up: the branches grouped and filtered, the keys, and what it
/// gives once closed. The branches are made up, so no repository is needed.
/// </summary>
[WindowThread]
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
            // Long enough that it widens, for its note to show whole beside it.
            Branch("feature/sign-in-with-the-new-provider", 24 * 6, stashed: true),
            Branch("origin/feature-on-origin", 24 * 3, remote: true)
        ]);

    const string longName = "origin/fix/null-default-variable-in-non-null-position-and-every-case-that-comes-after-it";

    // A name too long for it even half as wide again, so cut short, beside one that fits.
    internal static readonly GitBranches.Listing LongNames = new(
        "main",
        Branch("main", 15),
        [],
        [
            Branch("short", 2),
            Branch(longName, 24 * 90, remote: true),
            Branch("origin/fix/per-schema-builtin-scalars", 24 * 60, remote: true)
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
            })
            .Snapshot(
                """
                {
                  finding: [
                    Finding branches…
                  ],
                  all: [
                    DEFAULT BRANCH,
                        main  [15 hours],
                    RECENT BRANCHES,
                      ✓ asyncinterfaces-pin-netfx  [5 hours],
                        DiffEngine-21.2.0  [stashed changes · 2 days],
                        Update-to-TUnit-1.86.6  [3 days],
                    OTHER BRANCHES,
                        cleanup  [1 month],
                        feature/sign-in-with-the-new-provider  [stashed changes · 6 days],
                        origin/feature-on-origin  [3 days]
                  ],
                  filtered: [
                    RECENT BRANCHES,
                        Update-to-TUnit-1.86.6  [3 days],
                    OTHER BRANCHES,
                        cleanup  [1 month],
                        feature/sign-in-with-the-new-provider  [stashed changes · 6 days],
                        origin/feature-on-origin  [3 days]
                  ],
                  twoWords: [
                    RECENT BRANCHES,
                      ✓ asyncinterfaces-pin-netfx  [5 hours]
                  ],
                  noMatch: [
                    No branch matches. Enter makes a new one.
                  ],
                  none: [
                    No branches
                  ],
                  failed: [
                    Couldn't read the branches: fatal: not a git repository
                  ]
                }
                """);

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

        await Assert.That(BranchPicker.Place(new(900, 1010, 150, 22), size, area)).IsEqualTo(new(900, 590));
        await Assert.That(BranchPicker.Place(new(1800, 1010, 100, 22), size, area)).IsEqualTo(new(1540, 590));
        await Assert.That(BranchPicker.Place(new(10, 100, 100, 22), size, area)).IsEqualTo(new(10, 122));
    }

    // Long names widen it, to half as wide again at most. Past that a name is cut short, its
    // note left whole beside it, and hovering the name shows it whole, as GitHub Desktop has it.
    // Names that fit leave it as wide as it opens.
    [Test]
    public async Task LongNamesWidenItThenAreCutShort()
    {
        var shortNames = LongNames with
        {
            Other = [LongNames.Other[0]]
        };
        var longNames = LongNames;
        using var fits = new BranchPicker
        {
            Location = new(-20000, -20000)
        };
        fits.Show();
        var opened = fits.Width;
        await fits.Fill(Task.FromResult(shortNames));
        using var picker = new BranchPicker
        {
            Location = new(-20000, -20000)
        };
        picker.Show();
        await picker.Fill(Task.FromResult(longNames));

        await Assert.That(fits.Width).IsEqualTo(opened);
        await Assert.That(picker.Width).IsEqualTo((int) (opened * BranchPicker.WidestGrowth));

        var list = picker.Controls.OfType<ListBox>().Single();
        using var graphics = list.CreateGraphics();
        var rows = picker.Rows.ToList();
        var longIndex = rows.FindIndex(_ => _.Branch?.Name == longName);
        var shortIndex = rows.FindIndex(_ => _.Branch?.Name == "short");
        var cut = picker.LayoutRow(graphics, list.GetItemRectangle(longIndex), rows[longIndex].Branch!);
        var whole = picker.LayoutRow(graphics, list.GetItemRectangle(shortIndex), rows[shortIndex].Branch!);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        await Assert.That((cut.NameCut, whole.NameCut)).IsEqualTo((true, false));
        await Assert.That(cut.Note.Width).IsEqualTo(TextRenderer.MeasureText(graphics, cut.NoteText, list.Font, Size.Empty, flags).Width);
        await Assert.That(cut.Name.Right).IsLessThan(cut.Note.Left);
        await Assert.That(picker.NameTipAt(Middle(cut.Name))).IsEqualTo(longName);
        await Assert.That(picker.NameTipAt(Middle(cut.Note))).IsNull();
        await Assert.That(picker.NameTipAt(Middle(whole.Name))).IsNull();

        static Point Middle(Rectangle bounds) =>
            new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
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

    // A right-click entry closes the list, then runs, so a dialog it opens has the window to
    // itself. A heading has none.
    [Test]
    public async Task RightClickEntriesRunOnceClosed()
    {
        var renamed = new List<string>();
        using var picker = new BranchPicker
        {
            Location = new(-20000, -20000),
            MenuFor = (_, branch) =>
            [
                new("Rename…", () => renamed.Add(branch.Name)),
                null,
                new("Delete…", () => { })
            ]
        };
        picker.Show();
        await picker.Fill(Task.FromResult(Listing));
        var heading = picker.Rows.ToList().FindIndex(_ => _.Heading);
        var cleanup = picker.Rows.ToList().FindIndex(_ => _.Text == "cleanup");

        await Assert.That(picker.MenuAt(heading)).IsEmpty();
        var menu = picker.MenuAt(cleanup);
        await Assert.That(menu.Select(_ => _?.Text)).IsEquivalentTo(["Rename…", null, "Delete…"], CollectionOrdering.Matching);

        menu[0]!.Action();

        await Assert.That(picker.Visible).IsFalse();
        await Assert.That(picker.Chosen).IsNull();
        await Assert.That(renamed).IsEmpty();
        picker.FollowUp!();
        await Assert.That(renamed).IsEquivalentTo(["cleanup"]);
    }
}
