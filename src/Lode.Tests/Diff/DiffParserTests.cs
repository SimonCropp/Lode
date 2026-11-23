public class DiffParserTests
{
    const string twoHunks =
        """
        diff --git a/file.txt b/file.txt
        index 1111111..2222222 100644
        --- a/file.txt
        +++ b/file.txt
        @@ -2,4 +2,5 @@ class Thing
         two
        -three
        +three!
        +three and a half
         four
         five
        @@ -40 +41 @@
        -forty
        +forty!

        """;

    // Each line numbered on the side it is on, counted on from where its hunk starts; a count git
    // leaves out is 1; the index runs on across hunks.
    [Test]
    public async Task Hunks()
    {
        var diff = (TextDiff) DiffParser.Parse(twoHunks);

        await Assert.That(diff.Hunks.Count).IsEqualTo(2);
        await Assert.That(diff.Hunks[0].Header).IsEqualTo(new(2, 4, 2, 5));
        await Assert.That(diff.Hunks[0].Heading).IsEqualTo("@@ -2,4 +2,5 @@ class Thing");
        await Assert.That(diff.Hunks[1].Header).IsEqualTo(new(40, 1, 41, 1));
        await Assert.That(Describe(diff.Hunks[0].Lines))
            .IsEquivalentTo(
            [
                "0 Context 2 2 two",
                "1 Delete 3 _ three",
                "2 Add _ 3 three!",
                "3 Add _ 4 three and a half",
                "4 Context 4 5 four",
                "5 Context 5 6 five"
            ]);
        await Assert.That(Describe(diff.Hunks[1].Lines))
            .IsEquivalentTo(
            [
                "6 Delete 40 _ forty",
                "7 Add _ 41 forty!"
            ]);
        await Assert.That(diff.Added).IsEqualTo(3);
        await Assert.That(diff.Deleted).IsEqualTo(2);
        await Assert.That(diff.MaxLineNumber).IsEqualTo(42);
    }

    // What can be expanded: above the first hunk, which doesn't start the file; the second far
    // below it.
    [Test]
    public async Task Expansions()
    {
        var diff = (TextDiff) DiffParser.Parse(twoHunks);

        await Assert.That(diff.Hunks[0].Expansion).IsEqualTo(HunkExpansion.Up);
        await Assert.That(diff.Hunks[1].Expansion).IsEqualTo(HunkExpansion.Both);
    }

    // The marker marks the line before it, rather than being a line.
    [Test]
    public async Task NoNewlineAtEnd()
    {
        var diff = (TextDiff) DiffParser.Parse(
            """
            --- a/file.txt
            +++ b/file.txt
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file

            """);

        var lines = diff.Hunks[0].Lines;
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines.All(_ => _.NoTrailingNewline)).IsTrue();
    }

    [Test]
    public async Task Binary()
    {
        var diff = DiffParser.Parse(
            """
            diff --git a/image.png b/image.png
            index 1111111..2222222 100644
            Binary files a/image.png and b/image.png differ

            """);

        await Assert.That(diff).IsTypeOf<BinaryDiff>();
    }

    // A change of mode alone, or a file empty either side, has no hunks.
    [Test]
    public async Task Empty()
    {
        var diff = DiffParser.Parse(
            """
            diff --git a/script.sh b/script.sh
            old mode 100644
            new mode 100755

            """);

        await Assert.That(diff).IsTypeOf<EmptyDiff>();
        await Assert.That(DiffParser.Parse("")).IsTypeOf<EmptyDiff>();
    }

    // Windows line endings: the carriage return is kept in the text, for a patch, but not in what
    // is shown.
    [Test]
    public async Task CarriageReturns()
    {
        var diff = (TextDiff) DiffParser.Parse("--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\r\n+new\r\n");

        var added = diff.Hunks[0].Lines[1];
        await Assert.That(added.Text).IsEqualTo("+new\r");
        await Assert.That(added.Content).IsEqualTo("new");
    }

    // A line too long to show straight away, or too much altogether.
    [Test]
    public async Task Large()
    {
        var longLine = $"--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\n+{new string('x', DiffParser.MaxLineLength + 1)}\n";

        await Assert.That(DiffParser.Parse(longLine)).IsTypeOf<LargeDiff>();
        await Assert.That(DiffParser.Parse(longLine, evenIfLarge: true)).IsTypeOf<TextDiff>();
    }

    internal static IEnumerable<string> Describe(IEnumerable<DiffLine> lines) =>
        lines.Select(_ => $"{_.Index?.ToString() ?? "_"} {_.Kind} {_.OldNumber?.ToString() ?? "_"} {_.NewNumber?.ToString() ?? "_"} {_.Content}");
}
