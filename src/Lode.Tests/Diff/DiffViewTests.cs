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

        // Edited since, it is Modified, and still renamed.
        var (editedPicks, editedHint) = DiffView.PicksFor(new(Unique(), DiffSample.File with { Kind = GitChange.Modified, OldPath = "old.cs" }), DiffSample.Diff, hideWhitespace: false);
        await Assert.That(editedPicks).IsNull();
        await Assert.That(editedHint).IsEqualTo(renamedHint);
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

    // Dragged across, the text from the character pressed to the one let go, as an editor
    // selects it, not the whole lines; without the + or - before each.
    [Test]
    public async Task DraggingSelectsFromACharacterToAnother()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var canvas = view.Canvas;
        var options = Row(canvas, "        var options = BuildOptions();");

        Press(canvas, leftButtonDown, canvas.PointOf(new(options, 12), right: false));
        Press(canvas, mouseMove, canvas.PointOf(new(options + 3, 20), right: false));
        Press(canvas, leftButtonUp, canvas.PointOf(new(options + 3, 20), right: false));

        await Assert.That(canvas.SelectedText()).IsEqualTo(
            string.Join(
                Environment.NewLine,
                "options = BuildOptions();",
                "        var start = document.Reader.Position;",
                "        var written = document.Written;",
                "        var previous"));

        // Pressed again without dragging, nothing.
        Press(canvas, leftButtonDown, canvas.PointOf(new(options, 4), right: false));
        Press(canvas, leftButtonUp, canvas.PointOf(new(options, 4), right: false));
        await Assert.That(canvas.SelectedText()).IsEqualTo("");
    }

    // A double-click selects the word under it; a third click, the line, with its newline.
    [Test]
    public async Task DoubleAndTripleClicks()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var canvas = view.Canvas;
        var options = Row(canvas, "        var options = BuildOptions();");
        var inWord = canvas.PointOf(new(options, 14), right: false);

        Press(canvas, leftButtonDown, inWord);
        Press(canvas, leftButtonUp, inWord);
        Press(canvas, leftButtonDoubleClick, inWord);
        Press(canvas, leftButtonUp, inWord);
        await Assert.That(canvas.SelectedText()).IsEqualTo("options");

        Press(canvas, leftButtonDown, inWord);
        Press(canvas, leftButtonUp, inWord);
        await Assert.That(canvas.SelectedText()).IsEqualTo("        var options = BuildOptions();" + Environment.NewLine);
    }

    // Side by side, the selection is of the side dragged on, the old line or the new.
    [Test]
    public async Task SplitSelectsOneSide()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        view.SetSplit(true);
        var canvas = view.Canvas;
        var modified = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Modified);

        Press(canvas, leftButtonDown, canvas.PointOf(new(modified, 8), right: true));
        Press(canvas, mouseMove, canvas.PointOf(new(modified, 14), right: true));
        Press(canvas, leftButtonUp, canvas.PointOf(new(modified, 14), right: true));
        await Assert.That(canvas.SelectedText()).IsEqualTo("// The");

        Press(canvas, leftButtonDown, canvas.PointOf(new(modified, 8), right: false));
        Press(canvas, mouseMove, canvas.PointOf(new(modified, 16), right: false));
        Press(canvas, leftButtonUp, canvas.PointOf(new(modified, 16), right: false));
        await Assert.That(canvas.SelectedText()).IsEqualTo("document");
    }

    // Ctrl+A: every line, the hunks' headings left out, with no newline after the last.
    [Test]
    public async Task SelectAll()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var canvas = view.Canvas;

        canvas.SelectAll();

        var lines = canvas.Rows
            .Where(_ => _.Kind != DiffRowKind.Hunk)
            .Select(_ => (_.After ?? _.Before)!.Content);
        await Assert.That(canvas.SelectedText()).IsEqualTo(string.Join(Environment.NewLine, lines));
    }

    // A double-click's word: letters, digits and underscores together, or spaces, or else the
    // one character.
    [Test]
    public async Task Words()
    {
        const string line = "    var my_name2 = a.b;";

        static string WordAt(int character)
        {
            var (start, end) = DiffCanvas.WordAt(line, character);
            return line[start..end];
        }

        await Assert.That(WordAt(10)).IsEqualTo("my_name2");
        await Assert.That(WordAt(1)).IsEqualTo("    ");
        await Assert.That(WordAt(20)).IsEqualTo(".");
        await Assert.That(WordAt(99)).IsEqualTo(";");
        await Assert.That(DiffCanvas.WordAt("", 0)).IsEqualTo((0, 0));
    }

    // Read again as it was, as it is whenever anything in the repository changes: it stays as
    // it was, expanded, scrolled and selected.
    [Test]
    public async Task ReadAgainUnchanged()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var canvas = view.Canvas;
        canvas.ExpandWhole();
        canvas.TopRow = 50;
        canvas.Select(new(52, 4), new(54, 12), right: false);
        var hunks = canvas.Hunks;
        var selected = canvas.SelectedText();
        await Assert.That(selected).IsNotEmpty();

        view.Reload();
        await Read(view);

        await Assert.That(canvas.Hunks).IsSameReferenceAs(hunks);
        await Assert.That(canvas.TopRow).IsEqualTo(50);
        await Assert.That(canvas.SelectedText()).IsEqualTo(selected);
    }

    // Read again once the file changed, a line more added above: the whole file still shows,
    // and the same line is at the top, though it is a row further down.
    [Test]
    public async Task ReadAgainChanged()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var canvas = view.Canvas;
        canvas.ExpandWhole();
        var row = OldRow(canvas, 130);
        canvas.TopRow = row;
        await Assert.That(canvas.TopRow).IsEqualTo(row);

        using var changed = DiffSample.Stub(DiffSample.MoreAdded, DiffSample.MoreAddedLines);
        view.Reload();
        await Read(view);

        await Assert.That(canvas.Hunks.Count).IsEqualTo(1);
        await Assert.That(canvas.CanExpand).IsFalse();
        await Assert.That(canvas.Rows.Count(_ => _.Kind == DiffRowKind.Added)).IsEqualTo(9);
        await Assert.That(canvas.TopRow).IsEqualTo(row + 1);
        await Assert.That(canvas.Rows[canvas.TopRow].Before!.OldNumber).IsEqualTo(130);
    }

    // Read again part way through picking lines by dragging down their numbers: the drag goes
    // on, and what it picked is noted once it ends.
    [Test]
    public async Task ReadAgainWhilePicking()
    {
        var repo = Unique();
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(repo, DiffSample.File));
        var canvas = view.Canvas;
        var added = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Deleted) + 1;

        Press(canvas, leftButtonDown, Numbers(canvas, added));
        Press(canvas, mouseMove, Numbers(canvas, added + 1));
        view.Reload();
        await Read(view);
        Press(canvas, mouseMove, Numbers(canvas, added + 2));
        Press(canvas, leftButtonUp, Numbers(canvas, added + 2));

        // The three lines dragged over left out, of the nine.
        await Assert.That(canvas.Picks!.PickedCount).IsEqualTo(6);
        await Assert.That(CommitDrafts.Of(repo).TickOf(DiffSample.File.Path)).IsNull();
    }

    // The file changed part way through the drag: the drag goes on over the diff it started on,
    // and the file as it is now shows once it is let go, ticked whole, as the lines picked were
    // of the diff before.
    [Test]
    public async Task ChangedWhilePicking()
    {
        var repo = Unique();
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(repo, DiffSample.File));
        var canvas = view.Canvas;
        var added = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Deleted) + 1;

        Press(canvas, leftButtonDown, Numbers(canvas, added));
        Press(canvas, mouseMove, Numbers(canvas, added + 1));
        using var changed = DiffSample.Stub(DiffSample.MoreAdded, DiffSample.MoreAddedLines);
        view.Reload();
        await Read(view);
        Press(canvas, mouseMove, Numbers(canvas, added + 2));

        await Assert.That(canvas.Dragging).IsTrue();
        await Assert.That(canvas.Picks!.PickedCount).IsEqualTo(6);
        await Assert.That(canvas.Rows.Count(_ => _.Kind == DiffRowKind.Added)).IsEqualTo(8);

        Press(canvas, leftButtonUp, Numbers(canvas, added + 2));

        await Assert.That(canvas.Rows.Count(_ => _.Kind == DiffRowKind.Added)).IsEqualTo(9);
        await Assert.That(canvas.Picks!.PickedCount).IsEqualTo(10);
        await Assert.That(CommitDrafts.Of(repo).TickOf(DiffSample.File.Path)).IsTrue();
    }

    // An uncommitted change's menu offers to discard the line right-clicked, or the lines
    // selected, and the hunk it is in, asking first; a commit's, as History shows it, neither.
    [Test]
    public async Task DiscardOnlyFromAnUncommittedChange()
    {
        using var stub = DiffSample.Stub();
        using var view = await Shown(new(Unique(), DiffSample.File));
        var canvas = view.Canvas;
        var added = Row(canvas, "        var written = document.Written;");
        var context = Row(canvas, "        var options = BuildOptions();");
        var heading = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Hunk);

        await Assert.That(Discards(canvas, added)).IsEquivalentTo(["Discard line…", "Discard hunk…"], CollectionOrdering.Matching);
        await Assert.That(Discards(canvas, context)).IsEquivalentTo(["Discard line… (not now)", "Discard hunk…"], CollectionOrdering.Matching);
        await Assert.That(Discards(canvas, heading)).IsEquivalentTo(["Discard line… (not now)", "Discard hunk…"], CollectionOrdering.Matching);

        // The lines selected, from the deleted line to the third added after it, right-clicked
        // among them.
        var deleted = canvas.Rows.ToList().FindIndex(_ => _.Kind == DiffRowKind.Deleted);
        canvas.Select(new(deleted, 2), new(deleted + 3, 4), right: false);
        await Assert.That(Discards(canvas, deleted + 1)).IsEquivalentTo(["Discard 4 lines…", "Discard hunk…"], CollectionOrdering.Matching);

        // Asked first: how many lines, of which file, and that it goes to the Recycle Bin.
        var asked = new List<string>();
        var ask = ChangesPane.AskDiscard;
        ChangesPane.AskDiscard = (_, question) =>
        {
            asked.Add(question);
            return DialogResult.Cancel;
        };
        try
        {
            canvas.MenuCommands(deleted + 1, right: false).Single(_ => _?.Text == "Discard 4 lines…")!.Action();
            await view.Discarding;
            canvas.MenuCommands(added, right: false).Single(_ => _?.Text == "Discard hunk…")!.Action();
            await view.Discarding;
        }
        finally
        {
            ChangesPane.AskDiscard = ask;
        }

        await Assert.That(asked).IsEquivalentTo(
            [
                "Discard 4 changed lines in src/Scripting/ScriptRunner.cs?\n\nThe file as it is now goes to the Recycle Bin.",
                "Discard the hunk of 1 changed line in src/Scripting/ScriptRunner.cs?\n\nThe file as it is now goes to the Recycle Bin."
            ],
            CollectionOrdering.Matching);

        var commit = new GitCommit("abc", [], "Ada", DateTimeOffset.UnixEpoch, [], "Subject");
        using var committed = await Shown(new(Unique(), DiffSample.File, commit));
        await Assert.That(Discards(committed.Canvas, added)).IsEmpty();
    }

    // The menu's Discard items for a row, one above the other, those that can't be chosen there
    // saying so.
    static List<string> Discards(DiffCanvas canvas, int row) =>
        canvas.MenuCommands(row, right: false)
            .OfType<QuickMenu.Command>()
            .Where(_ => _.Text.StartsWith("Discard", StringComparison.Ordinal))
            .Select(_ => _.Enabled ? _.Text : $"{_.Text} (not now)")
            .ToList();

    // Discard hunk, asked and answered, in a repository: the file as it is goes to the Recycle
    // Bin, the hunk's changes are taken out of it on disk, the other hunk's staying, and the diff
    // is read again, the Changes pane told to read the repository again too. A line of the other
    // hunk left out of the commit stays left out.
    [Test]
    public async Task DiscardingAHunk()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.autocrlf", "false");
        var path = Path.Combine(repo, "tracked.txt");
        var lines = Enumerable.Range(1, 20).Select(_ => $"line {_}").ToArray();
        await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-am", "Twenty");
        lines[2] = "line three";
        lines[14] = "line fifteen";
        var both = string.Join('\n', lines) + "\n";
        await File.WriteAllTextAsync(path, both);
        var bin = new CodeCommandsTests.Bin(scratch);
        var recycler = DiffView.Recycler;
        var ask = ChangesPane.AskDiscard;
        DiffView.Recycler = _ => bin.Recycle;
        ChangesPane.AskDiscard = (_, _) => DialogResult.OK;
        string? told = null;
        void Told(string discardedIn) =>
            told = discardedIn;
        DiffView.LinesDiscarded += Told;
        try
        {
            using var view = await Shown(new(repo, new("tracked.txt", null, GitChange.Modified)));
            var canvas = view.Canvas;
            canvas.PickLine(Row(canvas, "line three"), right: false);

            canvas.MenuCommands(Row(canvas, "line fifteen"), right: false).Single(_ => _?.Text == "Discard hunk…")!.Action();
            await view.Discarding;
            await Read(view);

            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(both.Replace("line fifteen", "line 15"));
            await Assert.That(bin.Read("Repo/tracked.txt")).IsEqualTo(both);
            await Assert.That(canvas.Rows.Where(_ => _.Kind is DiffRowKind.Added or DiffRowKind.Deleted).Select(_ => (_.After ?? _.Before)!.Content))
                .IsEquivalentTo(["line 3", "line three"], CollectionOrdering.Matching);
            await Assert.That(told).IsEqualTo(repo);
            await Assert.That(canvas.Picks!.IsPicked(canvas.Rows[Row(canvas, "line three")].After!.Index!.Value)).IsFalse();
            await Assert.That(canvas.Picks.IsPicked(canvas.Rows[Row(canvas, "line 3")].Before!.Index!.Value)).IsTrue();
            await Assert.That(CommitDrafts.Of(repo).TickOf("tracked.txt")).IsNull();
        }
        finally
        {
            DiffView.LinesDiscarded -= Told;
            DiffView.Recycler = recycler;
            ChangesPane.AskDiscard = ask;
        }
    }

    const int mouseMove = 0x0200;
    const int leftButtonDown = 0x0201;
    const int leftButtonUp = 0x0202;
    const int leftButtonDoubleClick = 0x0203;

    // The row a line is drawn on, one above the other.
    static int Row(DiffCanvas canvas, string content) =>
        canvas.Rows.ToList().FindIndex(_ => (_.After ?? _.Before)?.Content == content);

    // The row of a line of the last commit's file.
    static int OldRow(DiffCanvas canvas, int number) =>
        canvas.Rows.ToList().FindIndex(_ => _.Before?.OldNumber == number);

    // Where a row's numbers are, to click or drag down.
    static Point Numbers(DiffCanvas canvas, int row) =>
        new(4, canvas.PointOf(new(row, 0), right: false).Y);

    // The button held for all but its letting go, as a drag has it.
    static void Press(DiffCanvas canvas, int message, Point point)
    {
        var held = 0;
        if (message != leftButtonUp)
        {
            held = 1;
        }

        NativeMethods.SendMessage(canvas.Handle, message, held, (point.Y << 16) | (point.X & 0xFFFF));
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
        await Read(view);
        return view;
    }

    static async Task Read(DiffView view)
    {
        await Pump.Until(() => view.Reading.IsCompleted);
        await view.Reading;
    }
}
