[STAThreadExecutor]
[NotInParallel("windows")]
public class DiffViewTests
{
    // Lines of an uncommitted change picked: the file partly ticked; every line picked again,
    // ticked whole.
    [Test]
    public async Task PickingLinesTicksTheFilePartly()
    {
        var repo = Unique();
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(repo, DiffSample.File));
        var canvas = view.Canvas;
        var added = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Added);

        canvas.PickLine(added, right: false);

        await Assert.That(CommitDrafts.Of(repo).TickOf(DiffSample.File.Path)).IsNull();
        await Assert.That(canvas.Picks!.PickedCount).IsEqualTo(8);

        canvas.PickLine(added, right: false);
        await Assert.That(CommitDrafts.Of(repo).TickOf(DiffSample.File.Path)).IsTrue();
    }

    // The strip beside the numbers: the whole run at once, then none of it.
    [Test]
    public async Task PickingARun()
    {
        var repo = Unique();
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(repo, DiffSample.File));
        var canvas = view.Canvas;
        var deleted = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Deleted);

        canvas.PickRun(deleted);

        // The deleted line and the seven added after it.
        await Assert.That(canvas.Picks!.PickedCount).IsEqualTo(1);
        canvas.PickRun(deleted);
        await Assert.That(canvas.Picks!.PickedCount).IsEqualTo(9);
    }

    // The file's tick clicked in Changes: the lines shown follow.
    [Test]
    public async Task FollowsTheTick()
    {
        var repo = Unique();
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(repo, DiffSample.File));

        var draft = CommitDrafts.Of(repo);
        draft.SetTick(DiffSample.File.Path, false);
        CommitDrafts.SetPicks(repo, DiffSample.File.Path, CommitDrafts.PicksOf(repo, DiffSample.File.Path, DiffSample.Diff));

        await Assert.That(view.Canvas.Picks!.PickedCount).IsEqualTo(0);
    }

    // A commit's lines aren't picked; nor, with whitespace hidden, an uncommitted change's,
    // which a click on the numbers says why.
    [Test]
    public async Task NoPicking()
    {
        using var stub = DiffSample.Stub();
        var commit = new GitCommit("abc", [], "Ada", DateTimeOffset.UnixEpoch, [], "Subject");
        using (var committed = await Shown(new(Unique(), DiffSample.File, commit)))
        {
            await Assert.That(committed.Canvas.Picks).IsNull();
        }

        var (picks, hint) = DiffView.PicksFor(new(Unique(), DiffSample.File), DiffSample.Diff, hideWhitespace: true);
        await Assert.That(picks).IsNull();
        await Assert.That(hint).Contains("whitespace");

        var (renamedPicks, renamedHint) = DiffView.PicksFor(new(Unique(), DiffSample.File with { Kind = GitChange.Renamed, OldPath = "old.cs" }), DiffSample.Diff, hideWhitespace: false);
        await Assert.That(renamedPicks).IsNull();
        await Assert.That(renamedHint).IsNotNull();
    }

    // Unified or split, kept for every diff after.
    [Test]
    public async Task Split()
    {
        using var stub = DiffSample.Stub();
        var settings = new Settings();
        using var view = await Shown(new(Unique(), DiffSample.File), settings);

        view.SetSplit(true);

        await Assert.That(settings.DiffSplit).IsTrue();
        await Assert.That(view.Canvas.Rows.Count(_ => _.Kind == DiffRowKind.Modified)).IsEqualTo(1);
    }

    // Along the header, as GitHub Desktop has them: the file, what happened to it over its status
    // icon, and how many lines were added and deleted over each count.
    [Test]
    public async Task HeaderTips()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var size = view.HeaderSize;
        var tips = new List<string>();
        for (var x = 0; x < size.Width; x++)
        {
            if (view.HeaderTipAt(new(x, size.Height / 2)) is { } tip &&
                (tips.Count == 0 || tips[^1] != tip))
            {
                tips.Add(tip);
            }
        }

        await Assert.That(tips).IsEquivalentTo(
            [
                "src/Scripting/ScriptRunner.cs, modified",
                "Modified",
                "8 lines added",
                "1 line deleted"
            ],
            CollectionOrdering.Matching);
    }

    // As GitHub Desktop says it of a diff with nothing in it.
    [Test]
    public async Task EmptyMessages()
    {
        await Assert.That(DiffView.EmptyMessage(new("a", null, GitChange.Untracked), false).Text).IsEqualTo("The file is empty.");
        await Assert.That(DiffView.EmptyMessage(new("a", "b", GitChange.Renamed), false).Text).IsEqualTo("The file was renamed but not changed.");
        await Assert.That(DiffView.EmptyMessage(new("a", null, GitChange.Modified), true)).IsEqualTo(("Only whitespace changes found.", "Show whitespace changes"));
        await Assert.That(DiffView.EmptyMessage(new("a", null, GitChange.Modified), false).Text).IsEqualTo("No content changes found.");
    }

    // Too big to show straight away: said, and shown when asked.
    [Test]
    public async Task Large()
    {
        using var stub = DiffSample.Stub(new LargeDiff(DiffSample.Patch));
        using var view = await Shown(new(Unique(), DiffSample.File));

        await Assert.That(view.MessageText).IsEqualTo("This diff is too big to show straight away.");
    }

    static string Unique() =>
        $@"C:\Repos\{Guid.NewGuid():N}";

    static async Task<DiffView> Shown(DiffSource source, Settings? settings = null)
    {
        var view = new DiffView(settings ?? new())
        {
            Size = new(800, 400)
        };
        view.CreateControl();
        view.Show(source);
        await Pump.Until(() => view.Reading.IsCompleted);
        await view.Reading;
        return view;
    }
}
