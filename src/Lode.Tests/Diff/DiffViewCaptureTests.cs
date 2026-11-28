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

    // C# in GitHub's colours: keywords, constants, comments, a block comment over two lines, an
    // interpolated string, a character, and a verbatim string over two lines, under the parts
    // that changed.
    [Test]
    public Task Coloured()
    {
        string[] lines =
        [
            "using System.Text;",
            "",
            "/// <summary>",
            "/// Says hello, as many times as asked.",
            "/// </summary>",
            "sealed class Greeter(string name)",
            "{",
            "    const int times = 3;",
            "",
            "    /* The greeting, its name quoted:",
            "       \"Ada\" says hello. */",
            "    public string Greet()",
            "    {",
            "        var builder = new StringBuilder();",
            "        for (var index = 0; index < times; index++)",
            "        {",
            "            builder.Append($\"\\\"{name}\\\" says hello\\t#{index + 1}\\n\");",
            "        }",
            "",
            "        return builder.ToString();",
            "    }",
            "",
            "    public static readonly string Path = @\"C:\\Greetings\\",
            "hello.txt\";",
            "",
            "    public bool IsQuiet => name.Length == 0 || name == null;",
            "}"
        ];
        // As git writes it, the old file's lines and the new's, a blank line both have a space.
        string[] hunk =
        [
            "@@ -1,22 +1,27 @@",
            " " + lines[0],
            " ",
            .. lines[2..7].Select(_ => " " + _),
            "-    const int times = 2;",
            "+" + lines[7],
            " ",
            "+" + lines[9],
            "+" + lines[10],
            .. lines[11..16].Select(_ => " " + _),
            "-            builder.Append(\"hello \" + name + '!');",
            "+" + lines[16],
            " " + lines[17],
            " ",
            " " + lines[19],
            " " + lines[20],
            " ",
            "+" + lines[22],
            "+" + lines[23],
            "+",
            "-    public bool IsQuiet => name.Length == 0;",
            "+" + lines[25],
            " " + lines[26]
        ];
        var patch = string.Join('\n', ["diff --git a/src/Greeter.cs b/src/Greeter.cs", "--- a/src/Greeter.cs", "+++ b/src/Greeter.cs", .. hunk, ""]);
        return Capture(
            new(DiffSample.Repo, new("src/Greeter.cs", null, GitChange.Modified), Commit()),
            diff: DiffParser.Parse(patch),
            newLines: lines,
            height: 760);
    }

    // The find bar under the header, every match of what it finds marked, the one gone to, the
    // second, stronger; a selection over a match beside it.
    [Test]
    public Task Find() =>
        Capture(
            new(DiffSample.Repo, DiffSample.File, Commit()),
            arrange: _ =>
            {
                _.OpenFind();
                _.FindBar.Box.Text = "document";
                _.Canvas.FindNext(forward: true);
                var row = _.Canvas.Rows.ToList().FindIndex(_ => _.After?.Content == "        var previous = document.Current;");
                _.Canvas.Select(new(row, 8), new(row, 28), right: false);
                _.ActiveControl = null;
            });

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

    static async Task Capture(DiffSource source, bool split = false, Action<DiffView>? arrange = null, FileDiff? diff = null, IReadOnlyList<string>? newLines = null, int height = 420)
    {
        using var stub = DiffSample.Stub(diff, newLines);
        var settings = new Settings
        {
            DiffSplit = split
        };
        using var form = new Form();
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);
        form.ClientSize = new(1000, height);
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
