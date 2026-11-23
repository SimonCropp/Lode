public class DiffExpansionTests
{
    // A 100 line file, line 30 and line 80 changed: two hunks far apart, each with three lines
    // of context either side.
    static IReadOnlyList<string> FileLines() =>
    [
        .. Enumerable.Range(1, 100)
            .Select(_ => $"line {_}")
    ];

    static IReadOnlyList<DiffHunk> Hunks(int first = 30, int second = 80)
    {
        var lines = FileLines();
        var patch = new StringBuilder("--- a/f\n+++ b/f\n");
        foreach (var changed in (int[]) [first, second])
        {
            patch.Append($"@@ -{changed - 3},7 +{changed - 3},7 @@\n");
            for (var number = changed - 3; number <= changed + 3; number++)
            {
                if (number == changed)
                {
                    patch.Append($"-old {number}\n+{lines[number - 1]}\n");
                    continue;
                }

                patch.Append($" {lines[number - 1]}\n");
            }
        }

        return ((TextDiff) DiffParser.Parse(patch.ToString())).Hunks;
    }

    [Test]
    public async Task Bottom()
    {
        var hunks = DiffExpansion.WithBottom(Hunks(), 100, 100);

        await Assert.That(hunks.Count).IsEqualTo(3);
        await Assert.That(hunks[2].IsBottom).IsTrue();
        await Assert.That(hunks[2].Expansion).IsEqualTo(HunkExpansion.Down);
        await Assert.That(hunks[2].Header).IsEqualTo(new(84, 17, 84, 17));

        // None once the last hunk reaches the end of the file.
        var atEnd = DiffExpansion.WithBottom(Hunks(30, 97), 100, 100);
        await Assert.That(atEnd.Count).IsEqualTo(2);
    }

    // Up from the first hunk: 20 lines above it, as context, numbered on both sides and never to
    // pick.
    [Test]
    public async Task Up()
    {
        var hunks = DiffExpansion.Expand(Hunks(), 0, up: true, FileLines())!;

        var first = hunks[0];
        await Assert.That(first.Header).IsEqualTo(new(7, 27, 7, 27));
        await Assert.That(first.Heading).IsEqualTo("@@ -7,27 +7,27 @@");
        await Assert.That(first.Lines[0]).IsEqualTo(new(DiffLineKind.Context, " line 7", null, 7, 7));
        await Assert.That(first.Expansion).IsEqualTo(HunkExpansion.Up);

        // And the rest of the way to the top.
        var top = DiffExpansion.Expand(hunks, 0, up: true, FileLines())!;
        await Assert.That(top[0].Header).IsEqualTo(new(1, 33, 1, 33));
        await Assert.That(top[0].Expansion).IsEqualTo(HunkExpansion.None);
        await Assert.That(DiffExpansion.Expand(top, 0, up: true, FileLines())).IsNull();
    }

    // Down from the first hunk, then up from the second, until they meet and are one.
    [Test]
    public async Task Merges()
    {
        var hunks = DiffExpansion.Expand(Hunks(), 0, up: false, FileLines())!;
        await Assert.That(hunks.Count).IsEqualTo(2);
        await Assert.That(hunks[0].Header).IsEqualTo(new(27, 27, 27, 27));
        await Assert.That(hunks[1].Expansion).IsEqualTo(HunkExpansion.Both);

        hunks = DiffExpansion.Expand(hunks, 1, up: true, FileLines())!;
        await Assert.That(hunks.Count).IsEqualTo(2);
        await Assert.That(hunks[1].Header).IsEqualTo(new(57, 27, 57, 27));

        // The three lines left between them are a short gap, all shown at once.
        await Assert.That(hunks[1].Expansion).IsEqualTo(HunkExpansion.Short);
        hunks = DiffExpansion.Expand(hunks, 1, up: true, FileLines())!;
        await Assert.That(hunks.Count).IsEqualTo(1);
        await Assert.That(hunks[0].Header).IsEqualTo(new(27, 57, 27, 57));
        await Assert.That(hunks[0].Lines.Select(_ => _.NewNumber).OfType<int>()).IsEquivalentTo(Enumerable.Range(27, 57));
    }

    // Down into the hunk for the rest of the file, which goes once there is nothing below.
    [Test]
    public async Task Down()
    {
        var hunks = DiffExpansion.WithBottom(Hunks(), 100, 100);

        hunks = DiffExpansion.Expand(hunks, 1, up: false, FileLines())!;
        await Assert.That(hunks.Count).IsEqualTo(2);
        await Assert.That(hunks[1].Header).IsEqualTo(new(77, 24, 77, 24));
        await Assert.That(hunks[1].Lines[^1].NewNumber).IsEqualTo(100);
    }

    [Test]
    public async Task Whole()
    {
        var hunks = DiffExpansion.ExpandWhole(DiffExpansion.WithBottom(Hunks(), 100, 100), FileLines())!;

        await Assert.That(hunks.Count).IsEqualTo(1);
        await Assert.That(hunks[0].Header).IsEqualTo(new(1, 100, 1, 100));
        await Assert.That(hunks[0].Lines.Count(_ => _.Kind == DiffLineKind.Delete)).IsEqualTo(2);
    }

    [Test]
    public async Task LinesOf()
    {
        await Assert.That(DiffExpansion.LinesOf("one\r\ntwo\r\n")).IsEquivalentTo(["one", "two"]);
        await Assert.That(DiffExpansion.LinesOf("one\ntwo")).IsEquivalentTo(["one", "two"]);
        await Assert.That(DiffExpansion.LinesOf("")).IsEmpty();
    }
}
