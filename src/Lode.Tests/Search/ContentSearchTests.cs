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

        await Verify(Text(results, scratch.Root))
            .Snapshot(
                """
                Searched 7 files for "alpha"

                {root}\hidden.txt:
                    1: hidden alpha

                {root}\latin.txt:
                    1: café alpha

                {root}\marked.txt:
                    1  first
                    2: alpha in marked

                {root}\plain.txt:
                    1: alpha beta
                    2  gamma
                    3: Alpha delta

                {root}\wide.txt:
                    1: wide alpha

                {root}\Sub\deep.cs:
                    1: // alpha

                7 matches across 6 files
                Left out: 1 binary file

                """);
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
            })
            .Snapshot(
                """
                {
                  hidden: [
                    hidden.txt,
                    latin.txt,
                    marked.txt,
                    plain.txt,
                    wide.txt,
                    Sub\deep.cs
                  ],
                  notHidden: [
                    latin.txt,
                    marked.txt,
                    plain.txt,
                    wide.txt,
                    Sub\deep.cs
                  ]
                }
                """);
    }

    // A file named as a device is, which a redirect to nul leaves behind in a shell that isn't
    // Windows': Windows opens the device for it, which can't be read as a file. It is counted
    // as one that couldn't be read, and the files after it are still found.
    [Test]
    public async Task AFileNamedAsADeviceIsLeftOut()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, "a.txt", "alpha");
        Write(scratch.Root, "z.txt", "alpha");
        var device = $@"\\?\{scratch.At("nul")}";
        await File.WriteAllTextAsync(device, "alpha");
        try
        {
            var results = await Find(new("alpha", ""), scratch.Root);

            await Assert.That(Paths(results, scratch.Root)).IsEquivalentTo(["a.txt", "z.txt"]);
            await Assert.That(results.Counts.Unreadable).IsEqualTo(1);
        }
        finally
        {
            // By the same name, or the scratch folder couldn't be deleted.
            File.Delete(device);
        }
    }

    // In a file that ends its lines as Windows does, $ is each line's end, and . stops there.
    [Test]
    public async Task ARegexTakesEachLineToEndHoweverItIsEnded()
    {
        const string text = "foo\r\nbar foo\r\nfoo\nfoo";

        static List<string> Found(string pattern)
        {
            var matcher = Matcher.Create(new(pattern, "", Regex: true), out _)!;
            var matches = new List<(int Start, int Length)>();
            matcher.Find(text, matches);
            return matches
                .Select(_ => $"{_.Start}:{text.Substring(_.Start, _.Length)}")
                .ToList();
        }

        await Assert.That(Found("foo$")).IsEquivalentTo(["0:foo", "9:foo", "14:foo", "18:foo"]);
        await Assert.That(Found("^foo.*")).IsEquivalentTo(["0:foo", "14:foo", "18:foo"]);
        await Assert.That(Found("r.*")).IsEquivalentTo(["7:r foo"]);
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
            })
            .Snapshot(
                """
                {
                  context:
                Searched 1 file for "needle"

                {root}\lines.txt:
                    1  line 1
                    2  line 2
                    3: line 3 has needle
                    4  line 4
                    5  line 5
                   ..
                   13  line 13
                   14  line 14
                   15: line 15 has needle
                   16: and needle again
                   17  line 17
                   18  line 18

                3 matches across 1 file
                ,
                  overLines:
                Searched 1 file for "needle\r?\nand" (regex)

                {root}\lines.txt:
                   15: line 15 has needle
                   16: and needle again

                1 match across 1 file

                }
                """);
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
            await Assert.That(found.Order(StringComparer.OrdinalIgnoreCase)).IsEquivalentTo(expected, CollectionOrdering.Matching);
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
            })
            .Snapshot(
                """
                {
                  Length: 302,
                  start: …aaaaaaaaa,
                  match: needle,
                  Column: 501
                }
                """);
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
            })
            .Snapshot(
                """
                {
                  any case: 0:Foo, 4:foo, 8:foo, 13:FOO, 18:foo, 22:foo,
                  bad pattern: error: The pattern can't be read: Invalid pattern '(' at offset 1. Not enough )'s.,
                  case sensitive: 4:foo, 8:foo, 18:foo, 22:foo,
                  dot as text: 27:f.o,
                  nothing: error: Type what to find.,
                  regex: 0:Foo, 4:foo, 8:foo, 13:FOO, 18:foo, 22:foo, 27:f.o,
                  regex over lines: 27:f.o\nB,
                  regex, whole word: 0:Foo, 4:foo, 13:FOO, 27:f.o,
                  whole word: 0:Foo, 4:foo, 13:FOO,
                  whole word, case sensitive: 4:foo
                }
                """);
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
            })
            .Snapshot(
                """
                {
                  notIgnored: [
                    bin\Tracked.cs,
                    Nested\Kept.txt,
                    src\Code.cs
                  ],
                  fromInside: [
                    src\Code.cs
                  ],
                  everything: [
                    run.log,
                    bin\Built.cs,
                    bin\Tracked.cs,
                    Nested\Kept.txt,
                    Nested\out\Left.txt,
                    src\Code.cs
                  ],
                  listed: [
                    repo,
                    repo\Nested,
                    repo
                  ]
                }
                """);
    }

    // In the code folder, which Lode watches, what git lists of a repository is kept until
    // something in it changes, so searching it again doesn't start git again. The window tests
    // of the Changes and History panes forget all GitCache knows when done, as PaneStubs does,
    // so this takes turns with them.
    [Test]
    [NotInParallel("windows")]
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

        await Verify(Text(results, scratch.Root))
            .Snapshot(
                """
                Searched 1 file for "alpha"

                {root}\repo\bin\Built.cs:
                    1: alpha

                1 match across 1 file
                Git couldn't list what it ignores in {root}\repo, so all of it was searched

                """);
    }

    [Test]
    public Task Where()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(scratch.At("Source"));
        Directory.CreateDirectory(scratch.At("Docs"));
        Directory.CreateDirectory(scratch.At("{One,Two}"));

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
                ["the same folder twice"] = Parsed("Source, source"),
                ["braces"] = Parsed("{Source,Docs}, *.{ts,tsx}, -{bin,obj}/, src/{App,Lib{,s}}/*.cs"),
                ["braces in quotes"] = Parsed("\"{One,Two}\"")
            })
            .Snapshot(
                """
                {
                  braces: {
                    roots: [
                      {root}\Source,
                      {root}\Docs
                    ],
                    includes: [
                      *.ts,
                      *.tsx,
                      src\App\*.cs,
                      src\Lib\*.cs,
                      src\Libs\*.cs
                    ],
                    excludes: [
                      bin\,
                      obj\
                    ]
                  },
                  braces in quotes: {
                    roots: [
                      {root}\{One,Two}
                    ],
                    includes: [],
                    excludes: []
                  },
                  empty: {
                    roots: [
                      {root}
                    ],
                    includes: [],
                    excludes: []
                  },
                  full folder and patterns: {
                    roots: [
                      {root}\Docs
                    ],
                    includes: [
                      *.cs
                    ],
                    excludes: [
                      *.min.js,
                      node_modules\,
                      *\obj\
                    ]
                  },
                  missing: error: There's no folder or file Nowhere.,
                  open folders: {
                    roots: [
                      {root}\Docs,
                      {root}\Source
                    ],
                    includes: [
                      *.md
                    ],
                    excludes: []
                  },
                  relative folder: {
                    roots: [
                      {root}\Source
                    ],
                    includes: [],
                    excludes: []
                  },
                  the same folder twice: {
                    roots: [
                      {root}\Source
                    ],
                    includes: [],
                    excludes: []
                  }
                }
                """);
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
                SearchWhere.WithFolder("<open folders>, *.cs", @"D:\Other"),
                SearchWhere.WithFolder(@"{C:\Code,C:\Docs}, *.{ts,tsx}, -{bin,obj}\", @"D:\Other")
            })
            .Snapshot(
                """
                [
                  D:\Other, *.cs, -bin\,
                  D:\Other,
                  <open folders>, *.cs,
                  D:\Other, *.{ts,tsx}, -{bin,obj}\
                ]
                """);

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
            })
            .Snapshot(
                """
                {
                  cs: [
                    src\App.cs,
                    src\obj\Gen.cs
                  ],
                  notMinified: [
                    src\App.js
                  ],
                  notObj: [
                    node_modules\lib.js,
                    src\App.cs,
                    src\App.js,
                    src\App.min.js
                  ]
                }
                """);
    }

    // A pattern with a slash in it is matched against the path from the folder searched, either
    // slash, its * going across them.
    [Test]
    public async Task PatternsWithASlashMatchThePath()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, @"src\App.cs", "alpha");
        Write(scratch.Root, @"src\obj\Gen.cs", "alpha");
        Write(scratch.Root, @"tests\App.cs", "alpha");
        Write(scratch.Root, @"tests\obj\Gen.cs", "alpha");

        var inSource = await Find(new("alpha", @"src\*.cs"), scratch.Root);
        var named = await Find(new("alpha", "SRC/App.?s"), scratch.Root);
        var notSourceObj = await Find(new("alpha", @"-src\obj\"), scratch.Root);
        var notAnyGen = await Find(new("alpha", @"-*\obj\Gen.cs"), scratch.Root);

        await Assert.That(Paths(inSource, scratch.Root)).IsEquivalentTo([@"src\App.cs", @"src\obj\Gen.cs"]);
        await Assert.That(Paths(named, scratch.Root)).IsEquivalentTo([@"src\App.cs"]);
        await Assert.That(Paths(notSourceObj, scratch.Root)).IsEquivalentTo([@"src\App.cs", @"tests\App.cs", @"tests\obj\Gen.cs"]);
        await Assert.That(Paths(notAnyGen, scratch.Root)).IsEquivalentTo([@"src\App.cs", @"tests\App.cs"]);
    }

    // ** as a part of its own, a slash after it, is any number of folders, none among them, as
    // in VS Code and .gitignore: src/**/*.cs takes the files in src itself, and -**/node_modules
    // leaves out the one at the top as well as those further down.
    [Test]
    public async Task AnyFoldersCanBeNone()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, "Top.cs", "alpha");
        Write(scratch.Root, @"src\App.cs", "alpha");
        Write(scratch.Root, @"src\obj\Gen.cs", "alpha");
        Write(scratch.Root, @"src\deep\obj\Deep.cs", "alpha");
        Write(scratch.Root, @"node_modules\lib.js", "alpha");
        Write(scratch.Root, @"src\node_modules\lib.js", "alpha");

        var inSource = await Find(new("alpha", "src/**/*.cs"), scratch.Root);
        var anywhere = await Find(new("alpha", @"**\*.cs"), scratch.Root);
        var inObj = await Find(new("alpha", "src/**/obj/*.cs"), scratch.Root);
        var noModules = await Find(new("alpha", "-**/node_modules"), scratch.Root);

        await Assert.That(Paths(inSource, scratch.Root)).IsEquivalentTo([@"src\App.cs", @"src\obj\Gen.cs", @"src\deep\obj\Deep.cs"]);
        await Assert.That(Paths(anywhere, scratch.Root)).IsEquivalentTo(["Top.cs", @"src\App.cs", @"src\obj\Gen.cs", @"src\deep\obj\Deep.cs"]);
        await Assert.That(Paths(inObj, scratch.Root)).IsEquivalentTo([@"src\obj\Gen.cs", @"src\deep\obj\Deep.cs"]);
        await Assert.That(Paths(noModules, scratch.Root)).IsEquivalentTo(["Top.cs", @"src\App.cs", @"src\obj\Gen.cs", @"src\deep\obj\Deep.cs"]);
    }

    // Braces are alternatives, as in VS Code's files to include, the commas between them not
    // ending the entry: *.{ts,tsx} is *.ts and *.tsx, rather than *.{ts and a folder tsx} that
    // isn't there. Braces in braces too, and in a folder's part of a pattern.
    [Test]
    public async Task BracesAreAlternatives()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, @"src\App.ts", "alpha");
        Write(scratch.Root, @"src\View.tsx", "alpha");
        Write(scratch.Root, @"src\Old.js", "alpha");
        Write(scratch.Root, @"src\Old.jsx", "alpha");
        Write(scratch.Root, @"tests\App.ts", "alpha");
        Write(scratch.Root, @"docs\App.ts", "alpha");
        Write(scratch.Root, @"bin\App.ts", "alpha");

        var typeScript = await Find(new("alpha", "*.{ts,tsx}"), scratch.Root);
        var nested = await Find(new("alpha", "*.{ts,js{,x}}"), scratch.Root);
        var inFolders = await Find(new("alpha", "{src,tests}/*.ts"), scratch.Root);
        var notInFolders = await Find(new("alpha", "-{bin,docs}/, *.ts"), scratch.Root);

        await Assert.That(Paths(typeScript, scratch.Root)).IsEquivalentTo([@"bin\App.ts", @"docs\App.ts", @"src\App.ts", @"src\View.tsx", @"tests\App.ts"]);
        await Assert.That(Paths(nested, scratch.Root)).IsEquivalentTo([@"bin\App.ts", @"docs\App.ts", @"src\App.ts", @"src\Old.js", @"src\Old.jsx", @"tests\App.ts"]);
        await Assert.That(Paths(inFolders, scratch.Root)).IsEquivalentTo([@"src\App.ts", @"tests\App.ts"]);
        await Assert.That(Paths(notInFolders, scratch.Root)).IsEquivalentTo([@"src\App.ts", @"tests\App.ts"]);
    }

    // A folder's name can have a comma in it, which otherwise ends an entry: in quotes it is the
    // one entry, and Where is given such a folder in them.
    [Test]
    public async Task AFolderWithACommaInItsName()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, @"Smith, John\notes.txt", "alpha");
        Write(scratch.Root, @"Smith, John\notes.md", "alpha");
        var folder = scratch.At("Smith, John");

        var where = SearchWhere.WithFolder(@"C:\Elsewhere, *.txt", folder);
        var results = await Find(new("alpha", where), scratch.Root);

        await Assert.That(where).IsEqualTo($"\"{folder}\", *.txt");
        await Assert.That(Paths(results, scratch.Root)).IsEquivalentTo([@"Smith, John\notes.txt"]);
    }

    // Folders one within another, as the open folders can be, and a file named that is in one
    // of them: each file is found the once, or Replace would replace in it twice.
    [Test]
    public async Task AFileUnderTwoOfTheFoldersIsFoundOnce()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Write(scratch.Root, "Top.cs", "alpha");
        Write(scratch.Root, @"src\App.cs", "alpha");
        Write(scratch.Root, @"src\obj\Gen.cs", "alpha");

        var outerFirst = await Find(new("alpha", $@"{scratch.Root}, src, src\obj\Gen.cs"), scratch.Root);
        var innerFirst = await Find(new("alpha", $@"src\obj\Gen.cs, src, {scratch.Root}"), scratch.Root);

        await Assert.That(Paths(outerFirst, scratch.Root)).IsEquivalentTo(["Top.cs", @"src\App.cs", @"src\obj\Gen.cs"]);
        await Assert.That(Paths(innerFirst, scratch.Root)).IsEquivalentTo([@"src\obj\Gen.cs", @"src\App.cs", "Top.cs"], CollectionOrdering.Matching);
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
            })
            .Snapshot(
                """
                {
                  rows: [
                    Row { Kind = Heading, File = -1, Line = -1, Summary = -1 }  Searched 2 files for "needle",
                    Row { Kind = Blank, File = -1, Line = -1, Summary = -1 }  ,
                    Row { Kind = File, File = 0, Line = -1, Summary = -1 }  {root}\a.txt:,
                    Row { Kind = Line, File = 0, Line = 0, Summary = -1 }      1  one,
                    Row { Kind = Line, File = 0, Line = 1, Summary = -1 }      2: needle,
                    Row { Kind = Line, File = 0, Line = 2, Summary = -1 }      3  three,
                    Row { Kind = Gap, File = 0, Line = -1, Summary = -1 }     ..,
                    Row { Kind = Line, File = 0, Line = 3, Summary = -1 }      6  six,
                    Row { Kind = Line, File = 0, Line = 4, Summary = -1 }      7: needle,
                    Row { Kind = Blank, File = 0, Line = -1, Summary = -1 }  ,
                    Row { Kind = File, File = 1, Line = -1, Summary = -1 }  {root}\b.txt:,
                    Row { Kind = Line, File = 1, Line = 0, Summary = -1 }      1: needle,
                    Row { Kind = Blank, File = 1, Line = -1, Summary = -1 }  ,
                    Row { Kind = Summary, File = -1, Line = -1, Summary = 0 }  3 matches across 2 files
                  ],
                  forward: [
                    4,
                    8,
                    11
                  ],
                  back: 3,
                  afterRemovingTheFirst:
                Searched 2 files for "needle"

                {root}\b.txt:
                    1: needle

                1 match across 1 file

                }
                """);
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
