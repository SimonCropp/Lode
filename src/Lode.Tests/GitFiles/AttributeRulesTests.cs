/// <summary>
/// Attributes as read in process, git check-attr the oracle; and a file whose times have changed
/// hashed as git would stage it, by the attributes and core.autocrlf, git status the oracle.
/// </summary>
public class AttributeRulesTests
{
    static readonly string[] paths =
    [
        "a.txt", "b.bin", "c.dat", "quoted name.txt", "quoted\ttab.txt", "octAl.txt", "x.c", "y.mine",
        "negated.txt", "dir/a", "sub/a.txt", "sub/deep/z.x", "f.u", "g.w", "h.info", "i.global",
        "sub/q.y", "staged/a.txt", "staged/deep/b.txt", "plain", "esc\a\b\f\n\r\t\v\\\"x"
    ];

    // Patterns plain and quoted with escapes of every kind, or not closed, macros, binary among
    // them, set, unset, unspecified and given values; a negative pattern and a folder's, which git
    // ignores for files; a folder's own .gitattributes over the top's, a macro there not allowed;
    // info/attributes over all, core.attributesFile under all, and a .gitattributes staged but
    // deleted, read from the index. The top's with a BOM, as Windows editors write one.
    [Test]
    public async Task GivesWhatGitGives()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(
            Path.Combine(repo, ".gitattributes"),
            (char)0xFEFF +
            """
            "esc\a\b\f\n\r\t\v\\\"x" diff=escaped
            "unended\
            # A comment
               *.txt text
            *.bin binary
            *.dat -text -diff merge=custom
            "quoted name.txt" eol=crlf
            "quoted\ttab.txt" diff=tabbed
            "oct\101l.txt" ident
            "unfinished
            *.c text=auto eol=lf
            [attr]mine text eol=crlf -diff
            *.mine mine
            !negated.txt text
            dir/ text
            sub/**/*.x filter=lfs
            *.u !text
            *.w working-tree-encoding=UTF-16
            *.y notallowed

            """);
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        await File.WriteAllTextAsync(Path.Combine(repo, "sub", ".gitattributes"), "*.txt -text\n[attr]notallowed text\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "attributes"), "*.info text\na.txt -diff\n");
        var global = scratch.At("attributes");
        await File.WriteAllTextAsync(global, "*.global diff=global\n*.txt diff=fromglobal\n");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.attributesFile", global.Replace('\\', '/'));
        Directory.CreateDirectory(Path.Combine(repo, "staged"));
        await File.WriteAllTextAsync(Path.Combine(repo, "staged", ".gitattributes"), "*.txt eol=lf\n");
        CodeCommandsTests.Scratch.Git(repo, "add", "staged/.gitattributes");
        File.Delete(Path.Combine(repo, "staged", ".gitattributes"));

        await Assert.That(InProcess(repo)).IsEquivalentTo(FromGit(repo), CollectionOrdering.Matching);
    }

    static List<string> FromGit(string repo)
    {
        var input = Encoding.UTF8.GetBytes(string.Concat(paths.Select(_ => _ + '\0')));
        var result = GitRun.Run(repo, null, input, "check-attr", "-a", "-z", "--stdin");
        if (!result.Succeeded)
        {
            throw new($"git check-attr failed: {result.Error}");
        }

        var parts = result.Output.Split('\0');
        var found = new List<string>();
        for (var index = 0; index + 2 < parts.Length; index += 3)
        {
            found.Add($"{parts[index]}: {parts[index + 1]}={parts[index + 2]}");
        }

        return found.Order(StringComparer.Ordinal).ToList();
    }

    static List<string> InProcess(string repo)
    {
        using var repository = GitRepository.Open(repo)!;
        var index = IndexFile.Read(repository.GitDir);
        var rules = new AttributeRules(repository, repo, FromIndex);
        var found = new List<string>();
        foreach (var path in paths)
        {
            foreach (var (name, state) in rules.For(path))
            {
                if (state == AttributeRules.Unspecified)
                {
                    continue;
                }

                found.Add($"{path}: {name}={Said(state)}");
            }
        }

        return found.Order(StringComparer.Ordinal).ToList();

        byte[]? FromIndex(string path)
        {
            for (var entry = 0; entry < index.Entries.Length; entry++)
            {
                if (index.PathText(entry) == path)
                {
                    return repository.Objects.TryRead(index.Entries[entry].Id)?.Data;
                }
            }

            return null;
        }
    }

    static string Said(string state)
    {
        if (state == AttributeRules.Set)
        {
            return "set";
        }

        if (state == AttributeRules.Unset)
        {
            return "unset";
        }

        return state;
    }

    // Each file committed, then its times put back, so git, and Lode, hash it to see whether it
    // changed: line endings as the attributes and core.autocrlf say, text found or not by what
    // the file has in it, filters git can't be asked for left alone. None has changed, but for
    // the one written to the same size.
    [Test]
    [Arguments("true")]
    [Arguments("input")]
    [Arguments("false")]
    public async Task HashesAsGitStages(string autocrlf)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.autocrlf", autocrlf);
        await File.WriteAllTextAsync(
            Path.Combine(repo, ".gitattributes"),
            """
            *.crlf eol=crlf
            *.lf eol=lf
            *.auto text=auto
            *.autolf text=auto eol=lf
            *.l1 crlf
            *.l2 -crlf
            *.l3 crlf=input
            *.utf8 working-tree-encoding=UTF-8 text
            *.id ident
            *.nofilter filter=notconfigured
            *.setfilter filter
            *.unsetfilter -filter
            *.unsure !text
            *.other text=other

            """);
        var crlf = "one\r\ntwo\r\n";
        var contents = new Dictionary<string, string>
        {
            ["a.crlf"] = crlf,
            ["b.lf"] = crlf,
            ["c.auto"] = crlf,
            ["d.autolf"] = crlf,
            ["e.l1"] = crlf,
            ["f.l2"] = crlf,
            ["g.l3"] = crlf,
            ["h.utf8"] = crlf,
            ["i.id"] = "no id here\r\n",
            ["j.nofilter"] = crlf,
            ["k.setfilter"] = crlf,
            ["l.unsetfilter"] = crlf,
            ["m.unsure"] = crlf,
            ["n.other"] = crlf,
            ["zero.auto"] = "a\0b\r\n",
            ["lonecr.auto"] = "a\rb\r\n",
            ["control.auto"] = "\u0001\u0002\u0003\u0004\r\n",
            ["eof.auto"] = "end\r\n\u001a",
            ["empty.auto"] = "",
            ["plain.txt"] = crlf,
            ["tabs.auto"] = "\ta\b\u001b\f\u007f\r\n"
        };
        foreach (var (name, content) in contents)
        {
            await File.WriteAllTextAsync(Path.Combine(repo, name), content);
        }

        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", "Files");
        await File.WriteAllTextAsync(Path.Combine(repo, "plain.txt"), "one\r\ntwX\r\n");
        var earlier = DateTime.UtcNow.AddMinutes(-5);
        foreach (var name in contents.Keys.Append(".gitattributes"))
        {
            File.SetLastWriteTimeUtc(Path.Combine(repo, name), earlier);
        }

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files.Select(_ => _.Path)).Contains("plain.txt");
    }

    // A filter git would run, ident with an $Id to put back, or an encoding other than UTF-8,
    // can't be hashed here, so the repository is left to git.
    [Test]
    [Arguments("*.f filter=upper", "[filter \"upper\"]\n\tclean = cat\n", "content\n", false)]
    [Arguments("*.f ident", "", "$Id$\n", false)]
    [Arguments("*.f working-tree-encoding=UTF-16LE-BOM", "", "content\n", true)]
    public async Task LeavesWhatOnlyGitCanHashToGit(string attributes, string config, string content, bool utf16)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), config);
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), attributes + "\n");
        if (utf16)
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "file.f"), content, Encoding.Unicode);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "file.f"), content);
        }

        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", "File");
        File.SetLastWriteTimeUtc(Path.Combine(repo, "file.f"), DateTime.UtcNow.AddMinutes(-5));

        await GitRun.LeftToGit(repo);
    }

    // A .gitattributes deleted but still staged says how a file is hashed, as git reads it from
    // the index, found there after what is staged before it.
    [Test]
    public async Task HashesByAttributesStagedButDeleted()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.autocrlf", "false");
        Directory.CreateDirectory(Path.Combine(repo, "zfolder"));
        await File.WriteAllTextAsync(Path.Combine(repo, "zfolder", ".gitattributes"), "*.txt text\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "zfolder", "a.txt"), "one\r\ntwo\r\n");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-q", "-m", "Files");
        File.Delete(Path.Combine(repo, "zfolder", ".gitattributes"));
        File.SetLastWriteTimeUtc(Path.Combine(repo, "zfolder", "a.txt"), DateTime.UtcNow.AddMinutes(-5));

        var state = await GitRun.Agree(repo);

        await Assert.That(state.Files).IsEquivalentTo([new GitFile("zfolder/.gitattributes", GitChange.Deleted)]);
    }
}
