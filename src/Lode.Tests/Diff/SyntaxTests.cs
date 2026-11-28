/// <summary>
/// The colours of code in a diff, each language's by its own small tokenizer: each run of a line
/// that isn't plain, as its kind and its text, runs of a kind with only whitespace between them
/// one run, as they are drawn.
/// </summary>
public class SyntaxTests
{
    // Picked by the file's extension, whatever its case; none for a file Lode has no colours for.
    [Test]
    public async Task ByExtension()
    {
        await Assert.That(SyntaxLanguage.For("src/Lode/Program.cs")!.Name).IsEqualTo("C#");
        await Assert.That(SyntaxLanguage.For("app.JS")!.Name).IsEqualTo("JavaScript");
        await Assert.That(SyntaxLanguage.For("app.tsx")!.Name).IsEqualTo("TypeScript");
        await Assert.That(SyntaxLanguage.For("package.json")!.Name).IsEqualTo("JSON");
        await Assert.That(SyntaxLanguage.For("MainWindow.xaml")!.Name).IsEqualTo("XML");
        await Assert.That(SyntaxLanguage.For("Lode.csproj")!.Name).IsEqualTo("XML");
        await Assert.That(SyntaxLanguage.For("index.html")!.Name).IsEqualTo("HTML");
        await Assert.That(SyntaxLanguage.For("site.css")!.Name).IsEqualTo("CSS");
        await Assert.That(SyntaxLanguage.For("build.py")!.Name).IsEqualTo("Python");
        await Assert.That(SyntaxLanguage.For("build.ps1")!.Name).IsEqualTo("PowerShell");
        await Assert.That(SyntaxLanguage.For(".github/workflows/build.yml")!.Name).IsEqualTo("YAML");
        await Assert.That(SyntaxLanguage.For("readme.md")).IsNull();
        await Assert.That(SyntaxLanguage.For("Makefile")).IsNull();
    }

    [Test]
    public async Task CSharp()
    {
        // A string with escaped quotes in it, and a comment after it.
        await Assert.That(Colours("x.cs", """var name = "a \"quoted\" word"; // done"""))
            .IsEquivalentTo(["Keyword var", """String "a \"quoted\" word" """.TrimEnd(), "Comment // done"], CollectionOrdering.Matching);

        // Keywords inside names, a verbatim name and members aren't keywords.
        await Assert.That(Colours("x.cs", "classify(interfaces, @class, publicKey, this.@int, other.Equals(int.MaxValue));"))
            .IsEquivalentTo(["Keyword this", "Keyword int"], CollectionOrdering.Matching);

        // Numbers in their forms, and the constants.
        await Assert.That(Colours("x.cs", "const long total = 0x1F + 1_000 + 2.5e-3f + 10m; bool done = true ?? null;"))
            .IsEquivalentTo(["Keyword const long", "Constant 0x1F", "Constant 1_000", "Constant 2.5e-3f", "Constant 10m", "Keyword bool", "Constant true", "Constant null"], CollectionOrdering.Matching);

        // Characters, the quote escaped in one.
        await Assert.That(Colours("x.cs", @"if (c == '\'' || c == 'x') return;"))
            .IsEquivalentTo(["Keyword if", @"String '\''", "String 'x'", "Keyword return"], CollectionOrdering.Matching);

        // An interpolated string whose code holds a string of its own; a verbatim one, whose
        // backslash escapes nothing.
        await Assert.That(Colours("x.cs", """var said = $"{name}: \"{(loud ? "HI" : "hi")}\"" + @"C:\temp\" + x;"""))
            .IsEquivalentTo(["Keyword var", """String $"{name}: \"{(loud ? "HI" : "hi")}\"" """.TrimEnd(), """String @"C:\temp\" """.TrimEnd()], CollectionOrdering.Matching);

        // A directive, the rest of a region's line text, not code with a character in it.
        await Assert.That(Colours("x.cs", "#region Don't stop")).IsEquivalentTo(["Keyword #region"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.cs", "#if DEBUG")).IsEquivalentTo(["Keyword #if"], CollectionOrdering.Matching);

        // A block comment in the middle of a line, code after it.
        await Assert.That(Colours("x.cs", "Call(/* first */ 1);")).IsEquivalentTo(["Comment /* first */", "Constant 1"], CollectionOrdering.Matching);
    }

    // A block comment, a verbatim string and a raw string, each going on over lines.
    [Test]
    public async Task CSharpOverLines() =>
        await Assert.That(ColoursOfLines(
                "x.cs",
                "/* a comment",
                "   still \"in\" it */ var x = 1;",
                "var text = @\"first",
                "second \"\" quote",
                "third\";",
                "var raw = \"\"\"",
                "    \"raw\" \"\" text",
                "    \"\"\";",
                "return x;"))
            .IsEquivalentTo(
            [
                "Comment /* a comment",
                "Comment    still \"in\" it */|Keyword var|Constant 1",
                "Keyword var|String @\"first",
                "String second \"\" quote",
                "String third\"",
                "Keyword var|String \"\"\"",
                "String     \"raw\" \"\" text",
                "String     \"\"\"",
                "Keyword return"
            ],
            CollectionOrdering.Matching);

    [Test]
    public async Task JavaScript()
    {
        // A regular expression, a slash in a class and an escaped one in it, beside divisions.
        await Assert.That(Colours("x.js", @"const re = /a\/b[/]c/gi; let half = total / 2 / count;"))
            .IsEquivalentTo(["Keyword const", @"String /a\/b[/]c/gi", "Keyword let", "Constant 2"], CollectionOrdering.Matching);

        // After return, an expression; a keyword as a member isn't one.
        await Assert.That(Colours("x.js", "return /x/.test(s) ? undefined : module.default;"))
            .IsEquivalentTo(["Keyword return", "String /x/", "Constant undefined"], CollectionOrdering.Matching);

        await Assert.That(Colours("x.js", "if (typeof value === 'it\\'s') { classes.push(`${value}px`); }"))
            .IsEquivalentTo(["Keyword if", "Keyword typeof", @"String 'it\'s'", "String `${value}px`"], CollectionOrdering.Matching);

        // A template literal's lines, a string in its code holding a backtick.
        await Assert.That(ColoursOfLines("x.js", "const html = `<p>${name + '`'}", "  </p>`; // end", "var done = true;"))
            .IsEquivalentTo(
            [
                "Keyword const|String `<p>${name + '`'}",
                "String   </p>`|Comment // end",
                "Keyword var|Constant true"
            ],
            CollectionOrdering.Matching);

        // TypeScript's own keywords, and its primitive types; neither is JavaScript's.
        await Assert.That(Colours("x.ts", "export interface Shape { kind: string; size?: number }"))
            .IsEquivalentTo(["Keyword export interface", "Constant string", "Constant number"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.js", "export interface Shape { kind: string }"))
            .IsEquivalentTo(["Keyword export"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Json() =>
        await Assert.That(Colours("x.json", """{"name": "lo\"de", "count": -3.5e2, "on": true, "none": null} // note"""))
            .IsEquivalentTo(
            [
                "Constant \"name\"",
                "String \"lo\\\"de\"",
                "Constant \"count\"",
                "Constant -3.5e2",
                "Constant \"on\"",
                "Constant true",
                "Constant \"none\"",
                "Constant null",
                "Comment // note"
            ],
            CollectionOrdering.Matching);

    [Test]
    public async Task Markup()
    {
        await Assert.That(Colours("x.xaml", """<Button Content="Hi &amp; bye" x:Name='go'/> <!-- note -->"""))
            .IsEquivalentTo(["Tag Button", "Attribute Content", "String \"Hi &amp; bye\"", "Attribute x:Name", "String 'go'", "Comment <!-- note -->"], CollectionOrdering.Matching);

        await Assert.That(Colours("x.xml", """<?xml version="1.0"?><p>a &lt; b</p><!DOCTYPE html>"""))
            .IsEquivalentTo(["Tag xml", "Attribute version", "String \"1.0\"", "Tag p", "Constant &lt;", "Tag p", "Tag !DOCTYPE", "Attribute html"], CollectionOrdering.Matching);

        // A tag's attributes over lines, as XAML has them, then a comment over lines.
        await Assert.That(ColoursOfLines(
                "x.xaml",
                "<Button",
                "    Content=\"Go\"",
                "    Click=\"OnClick\" />",
                "<!-- spans",
                "     lines --> <Grid>"))
            .IsEquivalentTo(
            [
                "Tag Button",
                "Attribute Content|String \"Go\"",
                "Attribute Click|String \"OnClick\"",
                "Comment <!-- spans",
                "Comment      lines -->|Tag Grid"
            ],
            CollectionOrdering.Matching);

        // HTML's script holds code, whose < is no tag.
        await Assert.That(ColoursOfLines("x.html", "<script>", "if (a<b) go();", "</script><b>"))
            .IsEquivalentTo(["Tag script", "", "Tag script|Tag b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Css()
    {
        await Assert.That(Colours("x.css", ".button:hover, #main > a { color: #fff; margin: 0 10px !important; }"))
            .IsEquivalentTo(
            [
                "Attribute .button:hover",
                "Attribute #main",
                "Tag a",
                "Constant color",
                "Constant #fff",
                "Constant margin",
                "Constant 0 10",
                "Keyword px !important"
            ],
            CollectionOrdering.Matching);

        // A property on a line of its own, in braces opened on the line before; a media query's
        // features as properties; a string, and a url's.
        await Assert.That(ColoursOfLines(
                "x.css",
                "@media (max-width: 600px) {",
                "  body {",
                "    font-family: \"Segoe UI\", sans-serif; /* a",
                "    comment */ background: url(img/back.png);",
                "  }",
                "}"))
            .IsEquivalentTo(
            [
                "Keyword @media|Constant max-width|Constant 600|Keyword px",
                "Tag body",
                "Constant font-family|String \"Segoe UI\"|Constant sans-serif|Comment /* a",
                "Comment     comment */|Constant background|Constant url|String img/back.png",
                "",
                ""
            ],
            CollectionOrdering.Matching);

        // SCSS's variables and line comments, which CSS hasn't.
        await Assert.That(Colours("x.scss", "$size: 2px; // big")).IsEquivalentTo(["Variable $size", "Constant 2", "Keyword px", "Comment // big"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Python()
    {
        await Assert.That(Colours("x.py", """def greet(name="wo\"rld"):  # say hi"""))
            .IsEquivalentTo(["Keyword def", "String \"wo\\\"rld\"", "Comment # say hi"], CollectionOrdering.Matching);

        await Assert.That(Colours("x.py", """classify = rb'\x00' if flag is not None else f"{a}" """.TrimEnd()))
            .IsEquivalentTo([@"String rb'\x00'", "Keyword if", "Keyword is not", "Constant None", "Keyword else", "String f\"{a}\""], CollectionOrdering.Matching);

        await Assert.That(Colours("x.py", "@app.route(\"/\")")).IsEquivalentTo(["Attribute @app.route", "String \"/\""], CollectionOrdering.Matching);

        // A docstring over lines.
        await Assert.That(ColoursOfLines("x.py", "    \"\"\"Says hello,", "    as asked.\"\"\"", "    return 1"))
            .IsEquivalentTo(["String \"\"\"Says hello,", "String     as asked.\"\"\"", "Keyword return|Constant 1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PowerShell()
    {
        await Assert.That(Colours("x.ps1", "if ($env:Path -like '*git*') { Write-Host \"Found $name`n\" } # done"))
            .IsEquivalentTo(["Keyword if", "Variable $env:Path", "Keyword -like", "String '*git*'", "String \"Found $name`n\"", "Comment # done"], CollectionOrdering.Matching);

        // Whatever their case; a command's name with a keyword in it isn't one; nor a parameter.
        await Assert.That(Colours("x.ps1", "$ok = $TRUE -AND $count -gt 10 | ForEach-Object -Begin { Return }"))
            .IsEquivalentTo(["Variable $ok", "Constant $TRUE", "Keyword -AND", "Variable $count", "Keyword -gt", "Constant 10", "Keyword Return"], CollectionOrdering.Matching);

        // A here-string's lines, quotes in them, and a block comment's.
        await Assert.That(ColoursOfLines("x.ps1", "$text = @\"", "line \"one\"", "\"@", "<# a", "comment #> exit"))
            .IsEquivalentTo(
            [
                "Variable $text|String @\"",
                "String line \"one\"",
                "String \"@",
                "Comment <# a",
                "Comment comment #>|Keyword exit"
            ],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Yaml()
    {
        await Assert.That(Colours("x.yml", "name: \"Build\" # the job")).IsEquivalentTo(["Tag name", "String \"Build\"", "Comment # the job"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.yml", "  - run: dotnet test")).IsEquivalentTo(["Tag run", "String dotnet test"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.yml", "url: https://example.com/a#b")).IsEquivalentTo(["Tag url", "String https://example.com/a#b"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.yml", "enabled: true")).IsEquivalentTo(["Tag enabled", "Constant true"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.yml", "retries: 3")).IsEquivalentTo(["Tag retries", "Constant 3"], CollectionOrdering.Matching);
        await Assert.That(Colours("x.yml", "base: &defaults")).IsEquivalentTo(["Tag base", "Variable &defaults"], CollectionOrdering.Matching);

        // A block's lines, for as long as they are further in than its key.
        await Assert.That(ColoursOfLines("x.yml", "    script: |", "      echo \"hi\" # not a comment", "", "      exit 1", "    next: no"))
            .IsEquivalentTo(
            [
                "Tag script|Keyword |",
                "String echo \"hi\" # not a comment",
                "",
                "String exit 1",
                "Tag next|Constant no"
            ],
            CollectionOrdering.Matching);
    }

    // Within a hunk, what goes on over lines goes on: a comment a line both sides have opens
    // into a line added, and a verbatim string into a line deleted on the old side and one added
    // on the new; a comment a deleted line opens takes in the line after it as the old file has
    // it, but not as the new file does. The file's lines unread, the hunk starts in code.
    [Test]
    public async Task OverLinesOfAHunk()
    {
        var diff = Parse(
            """
            @@ -1,6 +1,6 @@
             var a = 1; /* begins
            +still in the comment
             ends */ var b = 2;
             var c = @"verbatim
            -";
            +goes on"; var d = 3;
            -/* gone
             var e = 4; */
            """);
        var syntax = DiffSyntax.Of("x.cs", diff.Hunks, null)!;
        var hunk = diff.Hunks[0];

        await Assert.That(Runs(syntax, hunk, "still in the comment", old: false)).IsEquivalentTo(["Comment still in the comment"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "ends */ var b = 2;", old: false)).IsEquivalentTo(["Comment ends */", "Keyword var", "Constant 2"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "ends */ var b = 2;", old: true)).IsEquivalentTo(["Comment ends */", "Keyword var", "Constant 2"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "\";", old: true)).IsEquivalentTo(["String \""], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "goes on\"; var d = 3;", old: false)).IsEquivalentTo(["String goes on\"", "Keyword var", "Constant 3"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "var e = 4; */", old: true)).IsEquivalentTo(["Comment var e = 4; */"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "var e = 4; */", old: false)).IsEquivalentTo(["Keyword var", "Constant 4"], CollectionOrdering.Matching);
    }

    // A hunk whose start isn't known, the file's lines unread: in a block comment where the first
    // of opening one and closing one its lines do is closing it, the best guess there is;
    // otherwise in code.
    [Test]
    public async Task AHunkStartingInACommentIsGuessed()
    {
        var diff = Parse(
            """
            @@ -10,3 +10,4 @@
              * still the comment's
              */
            +var added = true;
             return;
            @@ -30,2 +31,3 @@
             x = 1; /* opens
            +inside */ y = 2;
             return;
            """);
        var syntax = DiffSyntax.Of("x.cs", diff.Hunks, null)!;

        await Assert.That(Runs(syntax, diff.Hunks[0], " * still the comment's", old: false)).IsEquivalentTo(["Comment  * still the comment's"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, diff.Hunks[0], "var added = true;", old: false)).IsEquivalentTo(["Keyword var", "Constant true"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, diff.Hunks[1], "x = 1; /* opens", old: false)).IsEquivalentTo(["Constant 1", "Comment /* opens"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, diff.Hunks[1], "inside */ y = 2;", old: false)).IsEquivalentTo(["Comment inside */", "Constant 2"], CollectionOrdering.Matching);
    }

    // Given the file's lines, a hunk starts as the file has it there, inside a verbatim string
    // opened above it, which no guess could tell; the old file too, where the hunk starts.
    [Test]
    public async Task AHunkStartsAsTheFileHasIt()
    {
        var diff = Parse(
            """
            @@ -2,3 +2,3 @@
             second
            -third";
            +third"; var b = 1;
             return;
            """);
        string[] lines = ["var text = @\"first", "second", "third\"; var b = 1;", "return;"];
        var layout = DiffLayout.Of(diff, lines, split: false);
        var syntax = DiffSyntax.Of("x.cs", layout.Hunks, lines)!;
        var hunk = layout.Hunks[0];

        await Assert.That(Runs(syntax, hunk, "second", old: false)).IsEquivalentTo(["String second"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "third\"; var b = 1;", old: false)).IsEquivalentTo(["String third\"", "Keyword var", "Constant 1"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "third\";", old: true)).IsEquivalentTo(["String third\""], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, hunk, "return;", old: true)).IsEquivalentTo(["Keyword return"], CollectionOrdering.Matching);
    }

    // Shown more of the file, what goes on over lines goes on across the gap that was between
    // two hunks: a comment a deleted line of the first opened, which the old file closed in a
    // deleted line of the second, is taken through the lines brought in, so the second's
    // deleted line, as the old file has it, starts in it, as it couldn't before, starting where
    // the new file, which has no comment, has it.
    [Test]
    public async Task OverTheGapOnceExpanded()
    {
        var diff = Parse(
            """
            @@ -1,3 +1,2 @@
             a();
            -/* old comment
             step(3);
            @@ -10,3 +9,2 @@
             step(10);
            -c(); */ d();
             e();
            """);
        string[] lines = ["a();", .. Enumerable.Range(3, 8).Select(_ => $"step({_});"), "e();"];
        var layout = DiffLayout.Of(diff, lines, split: false);
        var syntax = DiffSyntax.Of("x.cs", layout.Hunks, lines)!;

        await Assert.That(Runs(syntax, layout.Hunks[1], "c(); */ d();", old: true)).IsEmpty();

        var expanded = DiffExpansion.ExpandWhole(layout.Hunks, lines)!;
        var whole = expanded.Single();

        await Assert.That(Runs(syntax, whole, "c(); */ d();", old: true)).IsEquivalentTo(["Comment c(); */"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, whole, "step(6);", old: true)).IsEquivalentTo(["Comment step(6);"], CollectionOrdering.Matching);
        await Assert.That(Runs(syntax, whole, "step(6);", old: false)).IsEquivalentTo(["Constant 6"], CollectionOrdering.Matching);
    }

    static TextDiff Parse(string hunks) =>
        (TextDiff) DiffParser.Parse($"--- a/x.cs\n+++ b/x.cs\n{hunks}\n");

    // The runs of the first line of a hunk with the content, on a side.
    static List<string> Runs(DiffSyntax syntax, DiffHunk hunk, string content, bool old)
    {
        var line = hunk.Lines.First(_ => _.Content == content && (old ? _.OldNumber : _.NewNumber) != null);
        return syntax.RunsOf(hunk, line, old)
            .Select(_ => $"{_.Kind} {content.Substring(_.Start, _.Length)}")
            .ToList();
    }

    // The runs of a line, started in code.
    static List<string> Colours(string path, string line)
    {
        var runs = new List<SyntaxRun>();
        SyntaxLanguage.For(path)!.Tokenize(line, 0, runs);
        return runs.Select(_ => $"{_.Kind} {line.Substring(_.Start, _.Length)}").ToList();
    }

    // The runs of each of several lines, each started where the one before left off, a line's
    // runs between bars.
    static List<string> ColoursOfLines(string path, params string[] lines)
    {
        var language = SyntaxLanguage.For(path)!;
        var state = 0;
        var result = new List<string>();
        foreach (var line in lines)
        {
            var runs = new List<SyntaxRun>();
            state = language.Tokenize(line, state, runs);
            result.Add(string.Join('|', runs.Select(_ => $"{_.Kind} {line.Substring(_.Start, _.Length)}")));
        }

        return result;
    }
}
