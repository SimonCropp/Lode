public class DiffRowsTests
{
    const string patch =
        """
        --- a/file.txt
        +++ b/file.txt
        @@ -1,5 +1,5 @@
         one
        -var insert = index;
        -gone
        +var insertions = index;
        +var previous = script;
        +added
         five

        """;

    // One above the other: every deleted line of a run before every added one.
    [Test]
    public async Task Unified()
    {
        var rows = DiffRows.Build(Hunks(), split: false);

        await Assert.That(rows.Select(Describe))
            .IsEquivalentTo(
            [
                "Hunk",
                "Context one|one",
                "Deleted var insert = index;|",
                "Deleted gone|",
                "Added |var insertions = index;",
                "Added |var previous = script;",
                "Added |added",
                "Context five|five"
            ]);
    }

    // Side by side: each deleted line beside the added line in the same place in the run, the
    // added line left over on its own.
    [Test]
    public async Task Split()
    {
        var rows = DiffRows.Build(Hunks(), split: true);

        await Assert.That(rows.Select(Describe))
            .IsEquivalentTo(
            [
                "Hunk",
                "Context one|one",
                "Modified var insert = index;|var insertions = index;",
                "Modified gone|var previous = script;",
                "Added |added",
                "Context five|five"
            ]);
    }

    // No part is shown changed in a run with more added lines than deleted, as on github.com.
    [Test]
    public async Task ChangedOnlyWithAsManyEachSide()
    {
        var rows = DiffRows.Build(Hunks(), split: true);

        await Assert.That(rows.All(_ => _.BeforeChanged == null && _.AfterChanged == null)).IsTrue();

        var even = (TextDiff) DiffParser.Parse("--- a/f\n+++ b/f\n@@ -1 +1 @@\n-var insert = index;\n+var insertions = index;\n");
        var row = DiffRows.Build(even.Hunks, split: true)[1];
        await Assert.That(row.BeforeChanged).IsNull();
        await Assert.That(row.AfterChanged).IsEqualTo(new ChangedRange(10, 4));
    }

    // What is left once the start and end both share are taken off; nothing of a line the same.
    [Test]
    public async Task Changed()
    {
        await Assert.That(DiffRows.Changed("_document.Source.Index = insert;", "_document.Source.Position = insert;"))
            .IsEqualTo((new ChangedRange(17, 5), new ChangedRange(17, 8)));
        await Assert.That(DiffRows.Changed("same", "same")).IsEqualTo((null, null));
        await Assert.That(DiffRows.Changed("aaa", "aaaa")).IsEqualTo((null, new ChangedRange(3, 1)));
    }

    // What draws as one character is changed whole, rather than split where the two lines
    // differ: an emoji, two characters, changed for one sharing the first of them, or the second;
    // a letter with a mark on it; and an emoji of several, joined.
    [Test]
    public async Task ChangedWholeCharacters()
    {
        // 😀 to 😃, and 😀 to 𐘀, which shares the second.
        await Assert.That(DiffRows.Changed("'\U0001F600'", "'\U0001F603'")).IsEqualTo((new ChangedRange(1, 2), new ChangedRange(1, 2)));
        await Assert.That(DiffRows.Changed("a\U0001F600", "a\U00010600")).IsEqualTo((new ChangedRange(1, 2), new ChangedRange(1, 2)));

        // An e with an acute accent on it, to an e, and to an a with one.
        await Assert.That(DiffRows.Changed("cafe\u0301", "cafe")).IsEqualTo((new ChangedRange(3, 2), new ChangedRange(3, 1)));
        await Assert.That(DiffRows.Changed("e\u0301x", "a\u0301x")).IsEqualTo((new ChangedRange(0, 2), new ChangedRange(0, 2)));

        // A family of a man, a woman and a girl, to one with a boy.
        await Assert.That(DiffRows.Changed("\U0001F468\u200D\U0001F469\u200D\U0001F467!", "\U0001F468\u200D\U0001F469\u200D\U0001F466!"))
            .IsEqualTo((new ChangedRange(0, 8), new ChangedRange(0, 8)));
    }

    // Lines too long have nothing shown changed.
    [Test]
    public async Task LongLines()
    {
        var longer = new string('x', DiffRows.MaxChangedLineLength);
        var diff = (TextDiff) DiffParser.Parse($"--- a/f\n+++ b/f\n@@ -1 +1 @@\n-{longer}a\n+{longer}b\n");

        var row = DiffRows.Build(diff.Hunks, split: true)[1];

        await Assert.That(row.BeforeChanged).IsNull();
        await Assert.That(row.AfterChanged).IsNull();
    }

    static IReadOnlyList<DiffHunk> Hunks() =>
        ((TextDiff) DiffParser.Parse(patch)).Hunks;

    static string Describe(DiffRow row)
    {
        if (row.Kind == DiffRowKind.Hunk)
        {
            return "Hunk";
        }

        return $"{row.Kind} {row.Before?.Content}|{row.After?.Content}";
    }
}
