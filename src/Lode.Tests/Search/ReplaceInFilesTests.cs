/// <summary>
/// Find in Files' Replace: each file written back as it was read but for what is replaced, and
/// Undo and Redo putting back what was, or what Replace wrote.
/// </summary>
public class ReplaceInFilesTests
{
    // Byte order marks, encodings and line endings all as they were.
    [Test]
    public async Task EachFileIsWrittenBackAsItWasRead()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var marked = scratch.At("marked.txt");
        var wide = scratch.At("wide.txt");
        var latin = scratch.At("latin.txt");
        var mixed = scratch.At("mixed.txt");
        await File.WriteAllTextAsync(marked, "alpha\r\nbeta alpha\r\n", new UTF8Encoding(true));
        await File.WriteAllTextAsync(wide, "wide alpha\n", new UnicodeEncoding(false, true));
        await File.WriteAllBytesAsync(latin, [.. "caf"u8, 0xE9, .. " alpha"u8]);
        await File.WriteAllTextAsync(mixed, "alpha\nalpha\r\nalpha");

        var outcome = Replace([marked, wide, latin, mixed], new("alpha", ""), "omega");

        await Verify(
            new
            {
                outcome.Replaced,
                marked = Convert.ToHexString(await File.ReadAllBytesAsync(marked)),
                wide = Convert.ToHexString(await File.ReadAllBytesAsync(wide)),
                latin = Convert.ToHexString(await File.ReadAllBytesAsync(latin)),
                mixed = (await File.ReadAllTextAsync(mixed)).Replace("\r", "\\r").Replace("\n", "\\n"),
                outcome.Failed
            })
            .Snapshot(
                """
                {
                  Replaced: 7,
                  marked: EFBBBF6F6D6567610D0A62657461206F6D6567610D0A,
                  wide: FFFE770069006400650020006F006D006500670061000A00,
                  latin: 636166E9206F6D656761,
                  mixed: omega\nomega\r\nomega
                }
                """);
    }

    // Lines ended as Windows ends them: $ is still each line's end, and . stops before it, so
    // what replaces a line's end leaves the line ended as it was.
    [Test]
    public async Task ARegexLeavesEachLineEndedAsItWas()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var comments = scratch.At("comments.cs");
        var ends = scratch.At("ends.cs");
        await File.WriteAllTextAsync(comments, "one(); // first\r\ntwo(); // second\r\n");
        await File.WriteAllTextAsync(ends, "one()\r\ntwo()\r\n");

        Replace([comments], new(" //.*", "", Regex: true), "");
        Replace([ends], new(@"\)$", "", Regex: true), ");");

        await Assert.That(await File.ReadAllTextAsync(comments)).IsEqualTo("one();\r\ntwo();\r\n");
        await Assert.That(await File.ReadAllTextAsync(ends)).IsEqualTo("one();\r\ntwo();\r\n");
    }

    // A file that isn't UTF-8 is read a byte a character, and has no byte for most characters:
    // one of those in what replaces would be written as a question mark. The file is left, and
    // says why.
    [Test]
    public async Task WhatTheFilesEncodingCantHoldIsNotWritten()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var latin = scratch.At("latin.txt");
        byte[] bytes = [.. "caf"u8, 0xE9, .. " alpha"u8];
        await File.WriteAllBytesAsync(latin, bytes);

        var held = Replace([latin], new("alpha", ""), "é");
        var afterHeld = await File.ReadAllBytesAsync(latin);
        var notHeld = Replace([latin], new("caf", ""), "€");

        await Assert.That(held.Failed).IsEmpty();
        await Assert.That(afterHeld).IsEquivalentTo(new byte[] {0x63, 0x61, 0x66, 0xE9, 0x20, 0xE9}, CollectionOrdering.Matching);
        await Assert.That(notHeld.Failed.Single().Error).IsEqualTo("What replaces it has characters the file's encoding has none for.");
        await Assert.That(await File.ReadAllBytesAsync(latin)).IsEquivalentTo(afterHeld, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UndoThenRedo()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var first = scratch.At("first.txt");
        var second = scratch.At("second.txt");
        await File.WriteAllTextAsync(first, "alpha");
        await File.WriteAllTextAsync(second, "beta alpha");

        var outcome = Replace([first, second], new("alpha", ""), "omega");
        var replaced = new[] {await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second)};
        var undone = outcome.Undo!.Undo(null!, IntPtr.Zero);
        var afterUndo = new[] {await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second)};
        var redone = outcome.Undo.Redo(null!, IntPtr.Zero);

        await Verify(
            new
            {
                outcome.Undo.Name,
                replaced,
                undone,
                afterUndo,
                redone,
                afterRedo = new[] {await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second)}
            })
            .Snapshot(
                """
                {
                  Name: replace in 2 files,
                  replaced: [
                    omega,
                    beta omega
                  ],
                  afterUndo: [
                    alpha,
                    beta alpha
                  ],
                  afterRedo: [
                    omega,
                    beta omega
                  ]
                }
                """);
    }

    // A file changed after Replace is left as it is by Undo, which says so; the rest is undone.
    [Test]
    public async Task AFileChangedSinceIsNotUndone()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var kept = scratch.At("kept.txt");
        var edited = scratch.At("edited.txt");
        await File.WriteAllTextAsync(kept, "alpha");
        await File.WriteAllTextAsync(edited, "alpha");
        var outcome = Replace([kept, edited], new("alpha", ""), "omega");
        await File.WriteAllTextAsync(edited, "edited since");

        var undone = outcome.Undo!.Undo(null!, IntPtr.Zero);

        await Verify(
            new
            {
                undone.Result,
                failure = undone.Failure?.Replace(scratch.Root, "{root}"),
                kept = await File.ReadAllTextAsync(kept),
                edited = await File.ReadAllTextAsync(edited)
            })
            .Snapshot(
                """
                {
                  failure:
                Left as they are:
                {root}\edited.txt has changed since,
                  kept: alpha,
                  edited: edited since
                }
                """);
    }

    [Test]
    public async Task AReadOnlyFileIsLeftAndSaidSo()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var file = scratch.At("locked.txt");
        await File.WriteAllTextAsync(file, "alpha");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var outcome = Replace([file], new("alpha", ""), "omega");

            await Verify(
                new
                {
                    outcome.Replaced,
                    failed = outcome.Failed.Select(_ => $"{Path.GetFileName(_.Path)}: {_.Error}"),
                    undo = outcome.Undo != null,
                    file = await File.ReadAllTextAsync(file)
                })
                .Snapshot(
                    """
                    {
                      failed: [
                        locked.txt: It is read-only.
                      ],
                      undo: false,
                      file: alpha
                    }
                    """);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    // A write that fails part way, on a disk that fills as what replaces is longer, say, would
    // leave the file emptied for it: it is put back as it was, and said so, with nothing for
    // Undo. Here another handle holds a byte past the file's end, which only the longer text
    // reaches, so the write fails once the file is emptied, as a full disk's would.
    [Test]
    public async Task AWriteThatFailsLeavesTheFileAsItWas()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var file = scratch.At("full.txt");
        await File.WriteAllTextAsync(file, "alpha");

        ReplaceInFiles.Outcome outcome;
        using (var holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            holder.Lock(10, 1);
            outcome = Replace([file], new("alpha", ""), "omega omega omega");
        }

        await Assert.That(outcome.Failed.Select(_ => _.Path)).IsEquivalentTo([file]);
        await Assert.That(outcome.Changed).IsEmpty();
        await Assert.That(outcome.Undo).IsNull();
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("alpha");
    }

    // Should putting it back fail too, the share it is on gone, say, what it was is kept, and
    // the failure ends saying where: not where the next file's is kept, which would write over
    // it. The next file is still undone from its own.
    [Test]
    public async Task AFileThatCantBePutBackSaysWhereWhatItWasIsKept()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var first = scratch.At("first.txt");
        var second = scratch.At("second.txt");
        await File.WriteAllTextAsync(first, "alpha");
        await File.WriteAllTextAsync(second, "beta alpha");
        var write = ReplaceInFiles.Write;
        ReplaceInFiles.Write = (stream, bytes) =>
        {
            if (!string.Equals(stream.Name, first, StringComparison.OrdinalIgnoreCase))
            {
                write(stream, bytes);
                return;
            }

            stream.Write(bytes.AsSpan(0, 2));
            throw new IOException("The specified network name is no longer available.");
        };
        ReplaceInFiles.Outcome outcome;
        try
        {
            outcome = Replace([first, second], new("alpha", ""), "omega");
        }
        finally
        {
            ReplaceInFiles.Write = write;
        }

        var failure = outcome.Failed.Single();
        await Assert.That(failure.Path).IsEqualTo(first);
        await Assert.That(failure.Error).Contains(ReplaceInFiles.TempRoot);
        var kept = failure.Error[failure.Error.IndexOf(ReplaceInFiles.TempRoot, StringComparison.Ordinal)..];
        await Assert.That(await File.ReadAllTextAsync(kept)).IsEqualTo("alpha");

        outcome.Undo!.Undo(null!, IntPtr.Zero);

        await Assert.That(await File.ReadAllTextAsync(second)).IsEqualTo("beta alpha");
    }

    [Test]
    public Task Replacing()
    {
        string Replaced(string text, FindQuery query, string replacement, bool preserveCase = false) =>
            ReplaceInFiles.Replace(text, Matcher.Create(query, out _)!, replacement, preserveCase).Text;

        return Verify(
            new Dictionary<string, string>
            {
                ["text"] = Replaced("foo Foo FOO food", new("foo", ""), "bar"),
                ["whole word"] = Replaced("foo food", new("foo", "", WholeWord: true), "bar"),
                ["preserve case"] = Replaced("foo Foo FOO fOo", new("foo", ""), "bar", true),
                ["regex groups"] = Replaced("name = value", new(@"(\w+) = (\w+)", "", Regex: true), "$2 = $1"),
                ["named group"] = Replaced("v1.2", new(@"v(?<major>\d+)", "", Regex: true), "version ${major}"),
                ["regex, preserve case"] = Replaced("Alpha ALPHA", new("alpha", "", Regex: true), "omega", true)
            })
            .Snapshot(
                """
                {
                  named group: version 1.2,
                  preserve case: bar Bar BAR bar,
                  regex groups: value = name,
                  regex, preserve case: Omega OMEGA,
                  text: bar bar bar bard,
                  whole word: bar food
                }
                """);
    }

    // In a regular expression's replacement, \n, \t, \r and \\ are what they stand for, as in
    // VS Code: \n, and \r\n, the line ending most of the file's lines end with, Windows' in a
    // file with none. Anything else after a backslash, and plain text, as typed.
    [Test]
    public Task EscapesInARegexReplacement()
    {
        static string Replaced(string text, string find, string replacement, bool regex = true) =>
            ReplaceInFiles.Replace(text, Matcher.Create(new(find, "", Regex: regex), out _)!, replacement, false).Text
                .Replace("\r", "<CR>")
                .Replace("\n", "<LF>")
                .Replace("\t", "<TAB>");

        return Verify(
            new Dictionary<string, string>
            {
                ["line feed, lines ended as Windows ends them"] = Replaced("a;b\r\nc\r\n", ";", @";\n"),
                ["line feed, lines ended with line feeds"] = Replaced("a;b\nc\n", ";", @";\n"),
                ["line feed, mostly line feeds"] = Replaced("a;b\nc\nd\r\n", ";", @"\n"),
                ["line feed, no lines ended"] = Replaced("a;b", ";", @"\n"),
                ["carriage return and line feed, lines ended with line feeds"] = Replaced("a;b\nc\n", ";", @"\r\n"),
                ["carriage return"] = Replaced("a;b\n", ";", @"\r"),
                ["tab"] = Replaced("a;b", ";", @"\t"),
                ["backslash"] = Replaced("a;b", ";", @"\\"),
                ["backslash then n"] = Replaced("a;b", ";", @"\\n"),
                ["groups"] = Replaced("key=value", @"(\w+)=(\w+)", @"$1:\t$2"),
                ["anything else"] = Replaced("a;b", ";", @"\d\"),
                ["plain text"] = Replaced("a;b", ";", @"\n\t", regex: false)
            })
            .Snapshot(
                """
                {
                  anything else: a\d\b,
                  backslash: a\b,
                  backslash then n: a\nb,
                  carriage return: a<CR>b<LF>,
                  carriage return and line feed, lines ended with line feeds: a<LF>b<LF>c<LF>,
                  groups: key:<TAB>value,
                  line feed, lines ended as Windows ends them: a;<CR><LF>b<CR><LF>c<CR><LF>,
                  line feed, lines ended with line feeds: a;<LF>b<LF>c<LF>,
                  line feed, mostly line feeds: a<LF>b<LF>c<LF>d<CR><LF>,
                  line feed, no lines ended: a<CR><LF>b,
                  plain text: a\n\tb,
                  tab: a<TAB>b
                }
                """);
    }

    // \n in what replaces ends the line as the file's lines end: as Windows ends them in a file
    // whose lines end so, with a line feed in one whose lines end with one.
    [Test]
    public async Task ANewLineReplacedEndsTheLineAsTheFileDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var windows = scratch.At("windows.txt");
        var unix = scratch.At("unix.txt");
        await File.WriteAllTextAsync(windows, "one, two\r\nthree\r\n");
        await File.WriteAllTextAsync(unix, "one, two\nthree\n");

        var outcome = Replace([windows, unix], new(", ", "", Regex: true), @",\n\t");

        await Assert.That(outcome.Replaced).IsEqualTo(2);
        await Assert.That(await File.ReadAllTextAsync(windows)).IsEqualTo("one,\r\n\ttwo\r\nthree\r\n");
        await Assert.That(await File.ReadAllTextAsync(unix)).IsEqualTo("one,\n\ttwo\nthree\n");
    }

    // \n in the pattern matches a line ended as Windows ends it, as in VS Code: two lines are
    // found as one, joined, or swapped, the line between them ended as the file ends its lines.
    [Test]
    public async Task ANewLineInThePatternMatchesAWindowsLineEnding()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var braces = scratch.At("braces.cs");
        var swapped = scratch.At("swapped.txt");
        await File.WriteAllTextAsync(braces, "if (ready)\r\n{\r\n    go();\r\n}\r\n");
        await File.WriteAllTextAsync(swapped, "first\r\nsecond\r\n");

        var joined = Replace([braces], new(@"\)\n\{", "", Regex: true), ") {");
        var swap = Replace([swapped], new(@"^(\w+)\n(\w+)$", "", Regex: true), @"$2\n$1");

        await Assert.That(joined.Replaced).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(braces)).IsEqualTo("if (ready) {\r\n    go();\r\n}\r\n");
        await Assert.That(swap.Replaced).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(swapped)).IsEqualTo("second\r\nfirst\r\n");
    }

    // A line taken out of the results is left as it is, every match on it, the rest of its file
    // replaced.
    [Test]
    public async Task ALineTakenOutIsLeftAsItWas()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var file = scratch.At("notes.txt");
        await File.WriteAllTextAsync(file, "alpha one\nalpha two, alpha\nalpha three\n");
        var results = await ContentSearchTests.Find(new("alpha", ""), scratch.Root);
        results.RemoveLine(0, 1);

        var outcome = Replace(results, "omega");

        await Assert.That(results.FileAt(0).MatchCount).IsEqualTo(2);
        await Assert.That(outcome.Replaced).IsEqualTo(2);
        await Assert.That(outcome.Failed).IsEmpty();
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("omega one\nalpha two, alpha\nomega three\n");
    }

    // A file edited since it was searched, so that a line taken out of the results isn't where
    // it was, is left as it is, and said so: by its number, another line would be left in its
    // place, and it replaced.
    [Test]
    public async Task AFileWhoseLineTakenOutHasMovedIsLeft()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var file = scratch.At("notes.txt");
        await File.WriteAllTextAsync(file, "alpha one\nalpha two\n");
        var results = await ContentSearchTests.Find(new("alpha", ""), scratch.Root);
        results.RemoveLine(0, 1);
        await File.WriteAllTextAsync(file, "alpha zero\nalpha one\nalpha two\n");

        var outcome = Replace(results, "omega");

        await Assert.That(outcome.Replaced).IsEqualTo(0);
        await Assert.That(outcome.Failed.Single().Error).IsEqualTo("It has changed since it was searched: line 2, taken out of the results, isn't as it was.");
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("alpha zero\nalpha one\nalpha two\n");
    }

    // The preview, as the diff view lays it out: the file as it is against the file as Replace
    // would leave it, the line taken out of the results left as it is, \n ending the line as the
    // file's lines end, a hunk to each change far from the other, three lines either side.
    // Nothing is written.
    [Test]
    public async Task ThePreviewShowsWhatReplaceWouldDo()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var file = scratch.At("notes.txt");
        var lines = Enumerable.Range(1, 14).Select(_ => $"line {_}").ToList();
        lines[1] = "alpha two";
        lines[3] = "alpha four";
        lines[10] = "alpha eleven";
        var text = string.Join("\r\n", lines) + "\r\n";
        await File.WriteAllTextAsync(file, text);
        var results = await ContentSearchTests.Find(new(@"alpha (\w+)", "", Regex: true), scratch.Root);
        results.RemoveLine(0, results.FileAt(0).Lines.ToList().FindIndex(_ => _.Number == 4));
        var preview = new ReplacePreview(results.FileAt(0), Matcher.Create(results.Query, out _)!, @"$1\nbeta", false);

        var (diff, newLines) = preview.Read(evenIfLarge: false);
        var shown = (TextDiff) diff;
        var layout = DiffLayout.Of(shown, newLines, split: false);

        await Verify(
            new
            {
                hunks = shown.Hunks.Select(_ => _.Heading),
                rows = layout.Rows.Select(Describe),
                newLines = newLines!.Count,
                untouched = await File.ReadAllTextAsync(file) == text
            })
            .Snapshot(
                """
                {
                  hunks: [
                    @@ -1,5 +1,6 @@,
                    @@ -8,7 +9,8 @@
                  ],
                  rows: [
                    Hunk,
                    Context 1 line 1 | 1 line 1,
                    Deleted 2 alpha two |,
                    Added | 2 two,
                    Added | 3 beta,
                    Context 3 line 3 | 4 line 3,
                    Context 4 alpha four | 5 alpha four,
                    Context 5 line 5 | 6 line 5,
                    Hunk,
                    Context 8 line 8 | 9 line 8,
                    Context 9 line 9 | 10 line 9,
                    Context 10 line 10 | 11 line 10,
                    Deleted 11 alpha eleven |,
                    Added | 12 eleven,
                    Added | 13 beta,
                    Context 12 line 12 | 14 line 12,
                    Context 13 line 13 | 15 line 13,
                    Context 14 line 14 | 16 line 14
                  ],
                  newLines: 16,
                  untouched: true
                }
                """);
    }

    // The patch the preview writes, as git would write it: a line taken away whole, the last
    // line of a file without a line ending, the line ending at the end taken away, a line added
    // at the end, a replacement that changes nothing, an empty file.
    [Test]
    public Task PreviewPatches()
    {
        static string PatchOf(string before, string find, string replacement)
        {
            var replaced = new List<(int Start, int Length)>();
            var (after, ranges) = ReplaceInFiles.Replace(before, Matcher.Create(new(find, "", Regex: true), out _)!, replacement, false, null, replaced);
            return ReplacePreview.Patch("file.txt", before, after, replaced, ranges);
        }

        return Verify(
            new Dictionary<string, string>
            {
                ["a line taken away"] = PatchOf("one\nfoo\ntwo\n", @"foo\n", ""),
                ["the last line without a line ending"] = PatchOf("one\ntwo", "two", "three"),
                ["the last line ending taken away"] = PatchOf("one\ntwo\n", @"\n\z", ""),
                ["a line added at the end"] = PatchOf("one\n", @"\z", "two"),
                ["nothing changed"] = PatchOf("same\n", "same", "same"),
                ["an empty file"] = PatchOf("", "^", "first")
            })
            .Snapshot(
                """
                {
                  a line added at the end:
                --- a/file.txt
                +++ b/file.txt
                @@ -1,1 +1,2 @@
                 one
                +two
                \ No newline at end of file
                ,
                  a line taken away:
                --- a/file.txt
                +++ b/file.txt
                @@ -1,3 +1,2 @@
                 one
                -foo
                 two
                ,
                  an empty file:
                --- a/file.txt
                +++ b/file.txt
                @@ -0,0 +1,1 @@
                +first
                \ No newline at end of file
                ,
                  nothing changed:
                --- a/file.txt
                +++ b/file.txt
                ,
                  the last line ending taken away:
                --- a/file.txt
                +++ b/file.txt
                @@ -1,2 +1,2 @@
                 one
                -two
                +two
                \ No newline at end of file
                ,
                  the last line without a line ending:
                --- a/file.txt
                +++ b/file.txt
                @@ -1,2 +1,2 @@
                 one
                -two
                \ No newline at end of file
                +three
                \ No newline at end of file

                }
                """);
    }

    static string Describe(DiffRow row)
    {
        if (row.Kind == DiffRowKind.Hunk)
        {
            return "Hunk";
        }

        var before = "";
        if (row.Before is { } old)
        {
            before = $" {old.OldNumber} {old.Content}";
        }

        var after = "";
        if (row.After is { } line)
        {
            after = $" {line.NewNumber} {line.Content}";
        }

        return $"{row.Kind}{before} |{after}";
    }

    [Test]
    public Task InCaseOf() =>
        Verify(
            new Dictionary<string, string>
            {
                ["foo"] = ReplaceInFiles.InCaseOf("foo", "barBaz"),
                ["Foo"] = ReplaceInFiles.InCaseOf("Foo", "barBaz"),
                ["FOO"] = ReplaceInFiles.InCaseOf("FOO", "barBaz"),
                ["fOo"] = ReplaceInFiles.InCaseOf("fOo", "barBaz"),
                ["123"] = ReplaceInFiles.InCaseOf("123", "barBaz")
            })
            .Snapshot(
                """
                {
                  123: barBaz,
                  foo: barbaz,
                  Foo: BarBaz,
                  FOO: BARBAZ,
                  fOo: barBaz
                }
                """);

    static ReplaceInFiles.Outcome Replace(IReadOnlyList<string> files, FindQuery query, string replacement) =>
        ReplaceInFiles.Run(files.Select(_ => new FileResult(_, 0, [])).ToList(), Matcher.Create(query, out _)!, replacement, false, 0, null, Cancel.None);

    static ReplaceInFiles.Outcome Replace(FindResults results, string replacement) =>
        ReplaceInFiles.Run(results.Files.ToList(), Matcher.Create(results.Query, out _)!, replacement, false, 0, null, Cancel.None);
}
