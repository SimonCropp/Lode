#if DEBUG
/// <summary>
/// A file's diff drawn, as GitHub Desktop draws one. Debug only, as the other captures are.
/// </summary>
[WindowThread]
[NotInParallel("windows")]
public class DiffViewCaptureTests
{
    // A commit's change: one above the other, the old and new numbers side by side.
    [Test]
    public Task Unified() =>
        Capture(new(DiffSample.Repo, DiffSample.File, Commit()));

    // The old file beside the new, the line replaced beside the first of those replacing it.
    [Test]
    public Task Split() =>
        Capture(new(DiffSample.Repo, DiffSample.File, Commit()), split: true);

    // More of the file shown above the second hunk, from its arrow.
    [Test]
    public Task Expanded() =>
        Capture(
            new(DiffSample.Repo, DiffSample.File, Commit()),
            arrange: _ =>
            {
                var canvas = _.Canvas;
                var rows = canvas.Rows.ToList();
                var first = rows.FindIndex(_ => _.Kind == DiffRowKind.Hunk);
                var second = rows.FindIndex(first + 1, _ => _.Kind == DiffRowKind.Hunk);
                canvas.ExpandAt(second, up: true);
            });

    // An uncommitted change, its lines to pick: those picked blue, the if's braces left out.
    [Test]
    public Task Picked() =>
        Capture(
            new(DiffSample.Repo, DiffSample.File),
            arrange: _ =>
            {
                var diff = DiffSample.Diff;
                var unpicked = diff.Hunks
                    .SelectMany(_ => _.Lines)
                    .Where(_ => _.Text is "+        {" or "+        }")
                    .Select(_ => _.Index!.Value);
                CommitDrafts.SetPicks(DiffSample.Repo, DiffSample.File.Path, LinePicks.All(diff).With(unpicked, false));
            });

    // A picture changed: as it was beside as it is.
    [Test]
    public Task Image() =>
        Capture(
            new(DiffSample.Repo, new("images/logo.png", null, GitChange.Modified), Commit()),
            diff: new ImageDiff(Png(Color.SteelBlue, 48, 32), Png(Color.SeaGreen, 64, 32)));

    [Test]
    public Task Binary() =>
        Capture(
            new(DiffSample.Repo, new("data.bin", null, GitChange.Modified), Commit()),
            diff: new BinaryDiff());

    // Too big to show straight away: what is said, with the button under it that shows it anyway.
    [Test]
    public Task TooBig() =>
        Capture(
            new(DiffSample.Repo, new("data.json", null, GitChange.Modified), Commit()),
            diff: new LargeDiff(""));

    // What git said when it couldn't show the changes, long enough to wrap onto more lines.
    [Test]
    public Task Failed() =>
        Capture(
            new(DiffSample.Repo, DiffSample.File, Commit()),
            diff: new FailedDiff("fatal: unable to read tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904: the object file is empty or corrupt, as an interrupted fetch can leave it. Run git fsck to find which objects are damaged, then fetch them again from the remote."));

    static GitCommit Commit() =>
        new("abc", ["def"], "Ada Lovelace", DateTimeOffset.UnixEpoch, [], "Improve the scripts");

    static byte[] Png(Color color, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
            using var brush = new SolidBrush(Color.FromArgb(160, Color.White));
            graphics.FillEllipse(brush, width / 4, height / 4, width / 2, height / 2);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    static async Task Capture(DiffSource source, bool split = false, Action<DiffView>? arrange = null, FileDiff? diff = null)
    {
        using var stub = DiffSample.Stub(diff);
        var settings = new Settings
        {
            DiffSplit = split
        };
        using var form = new Form();
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);
        form.ClientSize = new(1000, 420);
        using var view = new DiffView(settings);
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        if (arrange != null &&
            source.IsUncommitted)
        {
            arrange(view);
        }

        view.Show(source);
        await Pump.Until(() => view.Reading.IsCompleted);
        await view.Reading;
        if (arrange != null &&
            !source.IsUncommitted)
        {
            arrange(view);
        }

        Application.DoEvents();
        await Verify(view);
    }
}
#endif
