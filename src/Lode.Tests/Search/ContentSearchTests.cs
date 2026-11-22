/// <summary>
/// Find in Files' search: what it reads, what it leaves out, and its results as Copy all
/// results gives them, which is how the Find Results tab lays them out.
/// </summary>
public class ContentSearchTests
{
    // Each encoding found, the one each file is in read, and a binary file left out.
    [Test]
    public async Task FindsInEachEncoding()
    {
        using var scratch = TextScratch();
        var results = await Find(new("alpha", ""), scratch.Root);

        await Verify(Text(results, scratch.Root));
    }

    [Test]
    public async Task HiddenFilesAreSearchedUnlessLeftOut()
    {
        using var scratch = TextScratch();
        var hidden = await Find(new("alpha", ""), scratch.Root);
        var notHidden = await Find(new("alpha", "", Hidden: false), scratch.Root);

        await Verify(
            new
            {
                hidden = Paths(hidden, scratch.Root),
                notHidden = Paths(notHidden, scratch.Root)
            });
    }

    // Lines either side of each match, ".." where lines are left out between them, and a match
    // over two lines putting both in.
    [Test]
    public async Task ContextAndGaps()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var lines = Enumerable.Range(1, 20).Select(_ => $"line {_}").ToList();
        lines[2] = "line 3 has needle";
        lines[14] = "line 15 has needle";
        lines[15] = "and needle again";
        await File.WriteAllTextAsync(scratch.At("lines.txt"), string.Join("\r\n", lines));

        var results = await Find(new("needle", ""), scratch.Root);
        var overLines = await Find(new(@"needle\r?\nand", "", Regex: true, ContextLines: 0), scratch.Root);

        await Verify(
            new
            {
                context = Text(results, scratch.Root),
                overLines = Text(overLines, scratch.Root)
            });
    }

    // A file is looked through as bytes before it is decoded, and left undecoded when they can't
    // hold a match. That must never leave out one that does: what is found in each is what
    // .NET's own search of its text finds, ı matching I and ſ S in any case.
    [Test]
    public async Task LookingThroughTheBytesFirstMissesNothing()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var files = new Dictionary<string, (byte[] Bytes, string Text)>
        {
            ["dotless.txt"] = Utf8("a fıle here"),
            ["longs.txt"] = Utf8("a ſtring here"),
            ["marked.txt"] = ([.. new UTF8Encoding(true).GetPreamble(), .. "File here"u8.ToArray()], "File here"),
            ["latin.txt"] = ([.. "caf"u8, 0xE9, .. " FILE"u8], "café FILE"),
            ["wide.txt"] = ([.. new UnicodeEncoding(false, true).GetPreamble(), .. Encoding.Unicode.GetBytes("wide file")], "wide file"),
            ["none.txt"] = Utf8("nothing to see")
        };
        foreach (var (name, (bytes, _)) in files)
        {
            await File.WriteAllBytesAsync(scratch.At(name), bytes);
        }

        foreach (var query in new FindQuery[] {new("file", ""), new("FILE", ""), new("string", ""), new("file", "", CaseSensitive: true)})
        {
            var found = Paths(await Find(query, scratch.Root), scratch.Root);
            var expected = files
                .Where(_ => _.Value.Text.Contains(query.Find, query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                .Select(_ => _.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await Assert.That(found.Order(StringComparer.OrdinalIgnoreCase)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    static (byte[] Bytes, string Text) Utf8(string text) =>
        (Encoding.UTF8.GetBytes(text), text);

    // A big file is looked through a megabyte at a time before it is read whole: a match across
    // two of those is still found, and a big file without one isn't read whole at all.
    [Test]
    public async Task AMatchAcrossTheChunksOfABigFile()
    {
        const int chunk = 1024 * 1024;
        using var scratch = new CodeCommandsTests.Scratch();
        var across = new byte[chunk * 3];
        Array.Fill(across, (byte) 'a');
        "Needle"u8.CopyTo(across.AsSpan(chunk - 3));
        await File.WriteAllBytesAsync(scratch.At("across.txt"), across);
        var without = new byte[chunk * 3];
        Array.Fill(without, (byte) 'a');
        await File.WriteAllBytesAsync(scratch.At("without.txt"), without);

        var anyCase = await Find(new("needle", ""), scratch.Root);
        var matchCase = await Find(new("Needle", "", CaseSensitive: true), scratch.Root);

        await Assert.That(Paths(anyCase, scratch.Root)).IsEquivalentTo(["across.txt"]);
        await Assert.That(Paths(matchCase, scratch.Root)).IsEquivalentTo(["across.txt"]);
        await Assert.That(anyCase.FileAt(0).Lines[0].Column).IsEqualTo(chunk - 2);
    }

    // A line of minified script is cut down round its first match.
    [Test]
    public async Task ALongLineIsCut()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        await File.WriteAllTextAsync(scratch.At("min.js"), new string('a', 500) + "needle" + new string('b', 500));

        var results = await Find(new("needle", ""), scratch.Root);
        var line = results.FileAt(0).Lines[0];

        await Verify(
            new
            {
                line.Text.Length,
                start = line.Text[..10],
                match = line.Text.Substring(line.Matches[0].Start, line.Matches[0].Length),
                line.Column
            });
    }

    [Test]
    public Task Matching()
    {
        const string text = "Foo foo food FOO _foo foo_ f.o\nBar";

        static string Found(FindQuery query)
        {
            var matcher = Matcher.Create(query, out var error);
            if (matcher == null)
            {
                return $"error: {error}";
            }

            var matches = new List<(int Start, int Length)>();
            matcher.Find(text, matches);
            return string.Join(", ", matches.Select(_ => $"{_.Start}:{text.Substring(_.Start, _.Length).Replace("\n", "\\n")}"));
        }

        return Verify(
            new Dictionary<string, string>
            {
                ["any case"] = Found(new("foo", "")),
                ["case sensitive"] = Found(new("foo", "", CaseSensitive: true)),
                ["whole word"] = Found(new("foo", "", WholeWord: true)),
                ["whole word, case sensitive"] = Found(new("foo", "", CaseSensitive: true, WholeWord: true)),
                ["regex"] = Found(new("f.o", "", Regex: true)),
                ["regex, whole word"] = Found(new("f.o", "", Regex: true, WholeWord: true)),
                ["regex over lines"] = Found(new(@"f\.o\nB", "", Regex: true)),
                ["dot as text"] = Found(new("f.o", "")),
                ["nothing"] = Found(new("", "")),
                ["bad pattern"] = Found(new("(", "", Regex: true))
            });
    }

    // In a repository, only what git doesn't ignore: a tracked file in an ignored folder still,
    // and a repository nested in it by what that one ignores. Asked to, everything.
    [Test]
    public async Task WhatGitIgnoresIsLeftOutUnlessAskedFor()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.At("repo");
        Directory.CreateDirectory(repo);
        CodeCommandsTests.Scratch.Git(repo, "init");
        Write(repo, ".gitignore", "bin/\n*.log\n");
        Write(repo, @"src\Code.cs", "alpha");
        Write(repo, @"bin\Built.cs", "alpha");
        Write(repo, @"bin\Tracked.cs", "alpha");
        Write(repo, "run.log", "alpha");
        CodeCommandsTests.Scratch.Git(repo, "add", "-f", "bin/Tracked.cs");
        var nested = Path.Combine(repo, "Nested");
        Directory.CreateDirectory(nested);
        CodeCommandsTests.Scratch.Git(nested, "init");
        Write(nested, ".gitignore", "out/\n");
        Write(nested, "Kept.txt", "alpha");
        Write(nested, @"out\Left.txt", "alpha");

        var listed = new List<string>();
        GitScope? List(string top)
        {
            listed.Add(Path.GetRelativePath(scratch.Root, top));
            return GitScope.List(top, Cancel.None);
        }

        var notIgnored = await Find(new("alpha", ""), repo, List);
        var fromInside = await Find(new("alpha", ""), Path.Combine(repo, "src"), List);
        var everything = await Find(new("alpha", "", IncludeIgnored: true), repo, List);

        await Verify(
            new
            {
                notIgnored = Paths(notIgnored, repo),
                fromInside = Paths(fromInside, repo),
                everything = Paths(everything, repo),
                listed
            });
    }

    // In the code folder, which Lode watches, what git lists of a repository is kept until
    // something in it changes, so searching it again doesn't start git again.
    [Test]
    public async Task WhatGitListsIsKeptInTheCodeFolderTillTheRepositoryChanges()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.At("Repo");
        Directory.CreateDirectory(repo);
        CodeCommandsTests.Scratch.Git(repo, "init");
        Write(repo, "first.txt", "alpha");
        GitCache.Watch(code.Root);
        try
        {
            // What git init wrote heard first, so it isn't taken for a change after the listing.
            await Task.Delay(1500);
            var listed = 0;
            GitScope? List()
            {
                listed++;
                return GitScope.List(repo, Cancel.None);
            }

            var first = GitCache.Searchable(repo, List);
            var again = GitCache.Searchable(repo, List);
            var whileQuiet = listed;

            Write(repo, "second.txt", "alpha");
            var waited = Stopwatch.StartNew();
            GitScope? afterChange = null;
            while (listed == whileQuiet &&
                   waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
                afterChange = GitCache.Searchable(repo, List);
            }

            await Assert.That(whileQuiet).IsEqualTo(1);
            await Assert.That(again).IsSameReferenceAs(first);
            await Assert.That(listed).IsEqualTo(2);
            await Assert.That(afterChange!.HasFile(Path.Combine(repo, "second.txt"))).IsTrue();
        }
        finally
        {
            GitCache.LetGo(code.Root);
        }
    }

    // Git not taking the repository, it is searched whole, and the results say so.
    [Test]
    public async Task ARepositoryGitCantListIsSearchedWhole()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.At("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Write(repo, @"bin\Built.cs", "alpha");

        var results = await Find(new("alpha", ""), repo, _ => null);

        await Verify(Text(results, scratch.Root));
    }

    [Test]
    public Task Where()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(scratch.At("Source"));
        Directory.CreateDirectory(scratch.At("Docs"));

        object Parsed(string text)
        {
            var where = SearchWhere.Parse(text, scratch.Root, [scratch.At("Docs"), scratch.At("Source")], out var error);
            if (where == null)
            {
                return $"error: {error?.Replace(scratch.Root, "{root}")}";
            }

            return new
            {
                roots = where.Roots.Select(_ => _.Replace(scratch.Root, "{root}")),
                includes = where.Includes.Select(_ => _.ToString()),
                excludes = where.Excludes.Select(_ => _.ToString())
            };
        }

        return Verify(
            new Dictionary<string, object>
            {
                ["empty"] = Parsed(""),
                ["relative folder"] = Parsed("Source"),
                ["full folder and patterns"] = Parsed($"{scratch.At("Docs")}, *.cs, -*.min.js, -node_modules/, -*\\obj\\"),
                ["open folders"] = Parsed("<open folders>, *.md"),
                ["missing"] = Parsed("Nowhere"),
                ["the same folder twice"] = Parsed("Source, source")
            });
    }

    // Opening the strip on another folder changes Where's folder, keeping its patterns, unless
    // it searches the open folders.
    [Test]
    public Task WhereWithAnotherFolder() =>
        Verify(
            new[]
            {
                SearchWhere.WithFolder(@"C:\Code, *.cs, -bin\", @"D:\Other"),
                SearchWhere.WithFolder("", @"D:\Other"),
                SearchWhere.WithFolder("<open folders>, *.cs", @"D:\Other")
            });

    [Test]
    public async Task IncludesAndExcludes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, @"src\App.cs", "alpha");
        Write(scratch.Root, @"src\App.min.js", "alpha");
        Write(scratch.Root, @"src\App.js", "alpha");
        Write(scratch.Root, @"src\obj\Gen.cs", "alpha");
        Write(scratch.Root, @"node_modules\lib.js", "alpha");

        var cs = await Find(new("alpha", "*.cs"), scratch.Root);
        var notMinified = await Find(new("alpha", "*.js, -*.min.js, -node_modules\\"), scratch.Root);
        var notObj = await Find(new("alpha", @"-*\obj\"), scratch.Root);

        await Verify(
            new
            {
                cs = Paths(cs, scratch.Root),
                notMinified = Paths(notMinified, scratch.Root),
                notObj = Paths(notObj, scratch.Root)
            });
    }

    [Test]
    public async Task RowsAndTheMatchesTheyGoTo()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, "a.txt", "one\nneedle\nthree\nfour\nfive\nsix\nneedle");
        Write(scratch.Root, "b.txt", "needle");
        var results = await Find(new("needle", "", ContextLines: 1), scratch.Root);

        var rows = Enumerable.Range(0, results.RowCount)
            .Select(_ => $"{results.RowAt(_)}  {results.TextOf(results.RowAt(_)).Replace(scratch.Root, "{root}")}")
            .ToList();
        var forward = new List<int>();
        for (var at = results.NextMatchRow(0, true); at >= 0; at = results.NextMatchRow(at + 1, true))
        {
            forward.Add(at);
        }

        results.Remove(0);

        await Verify(
            new
            {
                rows,
                forward,
                back = results.NextMatchRow(results.RowCount - 1, false),
                afterRemovingTheFirst = Text(results, scratch.Root)
            });
    }

    internal static CodeCommandsTests.Scratch TextScratch()
    {
        var scratch = new CodeCommandsTests.Scratch();
        File.WriteAllText(scratch.At("plain.txt"), "alpha beta\ngamma\nAlpha delta\n", new UTF8Encoding(false));
        File.WriteAllText(scratch.At("marked.txt"), "first\nalpha in marked\n", new UTF8Encoding(true));
        File.WriteAllText(scratch.At("wide.txt"), "wide alpha\n", new UnicodeEncoding(false, true));
        File.WriteAllBytes(scratch.At("latin.txt"), [.. "caf"u8, 0xE9, .. " alpha"u8]);
        File.WriteAllBytes(scratch.At("image.bin"), [.. "alpha"u8, 0, 1, 2]);
        File.WriteAllText(scratch.At("hidden.txt"), "hidden alpha\n");
        File.SetAttributes(scratch.At("hidden.txt"), FileAttributes.Hidden);
        Directory.CreateDirectory(scratch.At("Sub"));
        File.WriteAllText(scratch.At("Sub", "deep.cs"), "// alpha\n");
        return scratch;
    }

    static void Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static async Task<FindResults> Find(FindQuery query, string folder, Func<string, GitScope?>? list = null)
    {
        var where = SearchWhere.Parse(query.Where, folder, [], out var error) ??
                    throw new(error);
        var matcher = Matcher.Create(query, out error) ??
                      throw new(error);
        var queue = new ConcurrentQueue<FileResult>();
        var counts = new ContentSearch.Counts();
        var report = new SearchWalk.Report();
        await ContentSearch.Run(query, where, matcher, null, list ?? (_ => GitScope.List(_, Cancel.None)), queue, counts, report, Cancel.None);
        var results = new FindResults(query)
        {
            Counts = counts,
            Report = report,
            Done = true
        };
        while (queue.TryDequeue(out var result))
        {
            results.Add(result);
        }

        return results;
    }

    internal static string Text(FindResults results, string root) =>
        results.ToText().Replace(root, "{root}");

    static List<string> Paths(FindResults results, string root) =>
        results.Files.Select(_ => Path.GetRelativePath(root, _.Path)).ToList();
}
