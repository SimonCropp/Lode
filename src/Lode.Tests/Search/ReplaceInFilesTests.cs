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
            });
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
            });
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
            });
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
                });
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
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
            });
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
            });

    static ReplaceInFiles.Outcome Replace(IReadOnlyList<string> files, FindQuery query, string replacement) =>
        ReplaceInFiles.Run(files, Matcher.Create(query, out _)!, replacement, false, 0, null, Cancel.None);
}
