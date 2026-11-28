/// <summary>
/// Config read in process as git reads it, git config itself the oracle: what each file says,
/// what it includes, and what the environment adds.
/// </summary>
public class GitConfigTests
{
    // The user's config and the repository's, as GIT_CONFIG_GLOBAL and the git folder name them,
    // the system's left out, then keys the environment gives, all in the order git lists them.
    static Dictionary<string, string> Environment(string global, params (string Key, string Value)[] keys)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = global,
            ["GIT_CONFIG_COUNT"] = keys.Length.ToString(CultureInfo.InvariantCulture)
        };
        for (var index = 0; index < keys.Length; index++)
        {
            environment[$"GIT_CONFIG_KEY_{index}"] = keys[index].Key;
            environment[$"GIT_CONFIG_VALUE_{index}"] = keys[index].Value;
        }

        return environment;
    }

    static GitConfig Read(string repo, Dictionary<string, string> environment, string? branch = "main")
    {
        var gitDir = Path.Combine(repo, ".git");
        return GitConfig.Read(gitDir, gitDir, branch, environment.GetValueOrDefault);
    }

    // git config --list -z: each key, then a line break and its value where it has one, then a
    // NUL.
    static List<string> GitList(string repo, Dictionary<string, string> environment)
    {
        var result = GitRun.Run(repo, environment, null, "config", "--list", "--includes", "-z");
        if (!result.Succeeded)
        {
            throw new($"git config --list failed: {result.Error}");
        }

        // A value may have line breaks of its own: only the first follows the key.
        return result.Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(_ => new System.Text.RegularExpressions.Regex("\n").Replace(_, "=", 1))
            .ToList();
    }

    static List<string> Listed(GitConfig config) =>
        config
            .List()
            .Select(Line)
            .ToList();

    static string Line((string Key, string? Value) entry)
    {
        if (entry.Value == null)
        {
            return entry.Key;
        }

        return $"{entry.Key}={entry.Value}";
    }

    // Comments, sections old and new, a key on its section's line, subsections with quotes and
    // backslashes, values quoted, escaped, carried onto the next line, with comments after and
    // whitespace before, between and after, keys without values, CRLF lines and a BOM, a key set
    // twice, an include beside it and one by ~ that isn't there, and a backslash at the very end,
    // which goes on to nothing.
    [Test]
    public async Task ListsWhatGitLists()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var global = scratch.At("global.config");
        var text = string.Join(
            "\r\n",
            "before = any section",
            "# A comment",
            "; Another",
            "[core]",
            "\tbare = false",
            "[Section \"Sub \\\"Quoted\\\" \\\\ Name\"]",
            "\tKey = \"quoted ; not # a comment\" ; a comment",
            "\tescaped = tab\\there\\nnewline \\\"quote\\\" back\\\\slash bell\\b",
            "\tcontinued = first \\",
            "second",
            "\tspaced =   leading and   inner \t spaces   ",
            "\tFlag",
            "\tempty =",
            "\temptyQuoted = \"\"",
            "\tmixed = a\"b c\"d",
            "[old.Style]",
            "\tkey = value # comment",
            "[multi] value = one",
            "\tvalue = two",
            "[include]",
            "\tpath = included.config",
            "\tpath = ~/LodeNotThere.config",
            "[nospace]key=value",
            "[end]",
            "\tlast-key",
            "\ttrailing = at the very end \\");
        await File.WriteAllTextAsync(global, (char)0xFEFF + text);
        await File.WriteAllTextAsync(scratch.At("included.config"), "[included]\n\tvalue = from the include\n[spaced    \"sub\"]\n\tkey = after spaces\n# A comment at the very end");
        var environment = Environment(global, ("lode.FromEnvironment", "given"), ("lode.sub.Name", "sub"));

        var config = Read(repo, environment);

        await Assert.That(Listed(config)).IsEquivalentTo(GitList(repo, environment), CollectionOrdering.Matching);
        await Assert.That(config.Get("multi.value")).IsEqualTo("two");
        await Assert.That(config.GetAll("multi.value")).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);
        await Assert.That(config.Get("section.Sub \"Quoted\" \\ Name.flag")).IsNull();
        await Assert.That(config.IsSet("section.Sub \"Quoted\" \\ Name.flag")).IsTrue();
        await Assert.That(config.GetLocal("lode.fromenvironment")).IsNull();
        await Assert.That(config.Get("lode.fromenvironment")).IsEqualTo("given");
    }

    // A file git refuses, which can't be read here either.
    [Test]
    [Arguments("[unfinished")]
    [Arguments("[bad!name]")]
    [Arguments("[section \"unfinished]")]
    [Arguments("[section \"line\nbreak\"]")]
    [Arguments("[section \"escape at the end\\")]
    [Arguments("[section \"escaped\\\nline\"]")]
    [Arguments("[section unquoted]")]
    [Arguments("[section \"sub\" after]")]
    [Arguments("[section \"sub\"")]
    [Arguments("[section]\n1key = value")]
    [Arguments("[section]\nkey junk")]
    [Arguments("[section]\nkey = \"unfinished")]
    [Arguments("[section]\nkey = \"line\nbreak\"")]
    [Arguments("[section]\nkey = bad \\q escape")]
    [Arguments("[section]\nkey = \"quoted escape at the end\\")]
    public async Task RefusesWhatGitRefuses(string text)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var global = scratch.At("global.config");
        await File.WriteAllTextAsync(global, text);
        var environment = Environment(global);

        await Assert.That(GitRun.Run(repo, environment, null, "config", "--list").Succeeded).IsFalse();
        await Assert.That(() => Read(repo, environment)).Throws<GitFilesException>();
    }

    // One that includes itself goes on until git gives up, as here.
    [Test]
    public async Task RefusesIncludesWithoutEnd()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var global = scratch.At("global.config");
        await File.WriteAllTextAsync(global, "[include]\n\tpath = global.config\n");
        var environment = Environment(global);

        await Assert.That(GitRun.Run(repo, environment, null, "config", "--list", "--includes").Succeeded).IsFalse();
        await Assert.That(() => Read(repo, environment)).Throws<GitFilesException>();
    }

    // A key the environment gives with no section is one git refuses, as is a count with keys
    // missing, or one that isn't a count. An empty count is none.
    [Test]
    public async Task RefusesKeysTheEnvironmentGivesBadly()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var global = scratch.At("global.config");
        await File.WriteAllTextAsync(global, "");
        var noSection = Environment(global, ("nosection", "value"));
        var missing = Environment(global);
        missing["GIT_CONFIG_COUNT"] = "1";
        var notANumber = Environment(global);
        notANumber["GIT_CONFIG_COUNT"] = "many";
        var empty = Environment(global);
        empty["GIT_CONFIG_COUNT"] = "";

        foreach (var refused in new[] { noSection, missing, notANumber })
        {
            await Assert.That(GitRun.Run(repo, refused, null, "config", "--list").Succeeded).IsFalse();
            await Assert.That(() => Read(repo, refused)).Throws<GitFilesException>();
        }

        await Assert.That(Listed(Read(repo, empty))).IsEquivalentTo(GitList(repo, empty), CollectionOrdering.Matching);
    }

    // As git takes a boolean, or a number with a k, m or g: what isn't one is the fallback here,
    // where git would refuse it, as it would one too big for an int, such as a rename limit is.
    [Test]
    public async Task ReadsBooleansAndNumbersAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var global = scratch.At("global.config");
        string[] booleans = ["true", "YES", "on", "false", "No", "OFF", "1", "0", "7", "", "maybe"];
        string[] numbers = ["10", "2k", "1M", "1g", "-3", " 7 ", "x", "k", "99999999999"];
        var text = new StringBuilder("[flags]\n\tnone\n");
        for (var index = 0; index < booleans.Length; index++)
        {
            text.Append($"\tb{index} = \"{booleans[index]}\"\n");
        }

        text.Append("[numbers]\n");
        for (var index = 0; index < numbers.Length; index++)
        {
            text.Append($"\tn{index} = \"{numbers[index]}\"\n");
        }

        await File.WriteAllTextAsync(global, text.ToString());
        var environment = Environment(global);
        var config = Read(repo, environment);

        for (var index = 0; index < booleans.Length; index++)
        {
            var git = GitRun.Run(repo, environment, null, "config", "--type=bool", $"flags.b{index}");
            var expected = git.Succeeded ? git.Output.Trim() == "true" : (bool?)null;
            await Assert.That(GitConfig.ParseBool(booleans[index])).IsEqualTo(expected);
            await Assert.That(config.GetBool($"flags.b{index}", true)).IsEqualTo(expected ?? true);
        }

        for (var index = 0; index < numbers.Length; index++)
        {
            var git = GitRun.Run(repo, environment, null, "config", "--type=int", $"numbers.n{index}");
            var expected = -1;
            if (git.Succeeded &&
                int.TryParse(git.Output.Trim(), CultureInfo.InvariantCulture, out var number))
            {
                expected = number;
            }

            await Assert.That(config.GetInt($"numbers.n{index}", -1)).IsEqualTo(expected);
        }

        await Assert.That(config.GetBool("flags.none", false)).IsTrue();
        await Assert.That(config.GetBool("flags.missing", true)).IsTrue();
        await Assert.That(config.GetInt("numbers.missing", 5)).IsEqualTo(5);
    }

    // Included on a condition: the git folder by its path, relative or absolute, a trailing slash
    // for what is under one, ./ from the including file, ~ for the home folder, whatever the case
    // with gitdir/i, and the branch checked out. What git includes, and only that.
    [Test]
    public async Task IncludesOnConditionsAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var global = scratch.At("global.config");
        await File.WriteAllTextAsync(global, "");
        var gitDir = Path.Combine(repo, ".git").Replace('\\', '/');
        var conditions = new[]
        {
            $"gitdir:{gitDir}",
            $"gitdir:{gitDir}/",
            "gitdir:Repo/",
            "gitdir:Repo/.git",
            "gitdir:Other/",
            $"gitdir:{gitDir.ToUpperInvariant()}",
            $"gitdir/i:{gitDir.ToUpperInvariant()}",
            "gitdir/i:REPO/",
            "gitdir:./",
            "gitdir:./other/",
            "gitdir:~/LodeNowhere/",
            "gitdir:**/Re?o/**",
            "onbranch:main",
            "onbranch:ma*",
            "onbranch:feature/",
            "onbranch:other"
        };
        var local = new StringBuilder();
        for (var index = 0; index < conditions.Length; index++)
        {
            await File.WriteAllTextAsync(scratch.At($"condition{index}.config"), $"[conditions]\n\tc{index} = included\n");
            local.Append($"[includeIf \"{conditions[index]}\"]\n\tpath = {scratch.At($"condition{index}.config").Replace('\\', '/')}\n");
        }

        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), local.ToString());
        var environment = Environment(global);

        var config = Read(repo, environment);
        var detached = Read(repo, environment, branch: null);

        await Assert.That(Listed(config)).IsEquivalentTo(GitList(repo, environment), CollectionOrdering.Matching);
        await Assert.That(config.Uncertain).IsFalse();
        await Assert.That(detached.IsSet("conditions.c12")).IsFalse();

        CodeCommandsTests.Scratch.Git(repo, "switch", "-q", "-c", "feature/one");
        await Assert.That(Listed(Read(repo, environment, "feature/one"))).IsEquivalentTo(GitList(repo, environment), CollectionOrdering.Matching);
    }

    // A condition git knows and this doesn't, on what the config has, can't be told here, so
    // the repository is left to git.
    [Test]
    public async Task LeavesConditionsItCantTellToGit()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "[includeIf \"hasconfig:remote.*.url:**/*\"]\n\tpath = remote.config\n");

        await Assert.That(Read(repo, Environment(scratch.At("none.config"))).Uncertain).IsTrue();
        await GitRun.LeftToGit(repo);
    }

    // The system's config as GIT_CONFIG_SYSTEM names it, or none with GIT_CONFIG_NOSYSTEM, and
    // the user's in their home and XDG folders without GIT_CONFIG_GLOBAL.
    [Test]
    public async Task ReadsTheFilesTheEnvironmentNames()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var system = scratch.At("system.config");
        await File.WriteAllTextAsync(system, "[system]\n\tkey = system\n");
        var global = scratch.At("global.config");
        await File.WriteAllTextAsync(global, "[global]\n\tkey = global\n");
        var named = new Dictionary<string, string>
        {
            ["GIT_CONFIG_SYSTEM"] = system,
            ["GIT_CONFIG_GLOBAL"] = global,
            ["GIT_CONFIG_COUNT"] = "0"
        };
        var falseNoSystem = new Dictionary<string, string>(named)
        {
            ["GIT_CONFIG_NOSYSTEM"] = "false"
        };

        await Assert.That(Listed(Read(repo, named))).IsEquivalentTo(GitList(repo, named), CollectionOrdering.Matching);
        await Assert.That(Listed(Read(repo, falseNoSystem))).IsEquivalentTo(GitList(repo, falseNoSystem), CollectionOrdering.Matching);

        // As Lode runs git, with the user's own files: some keys of theirs, the same keys git has.
        var users = Read(repo, UsersEnvironment);
        await Assert.That(users.Get("remote.origin.url")).IsEqualTo(GitRun.Output(repo, "config", "remote.origin.url"));

        // The process's own, but for keys another test may have given its git meanwhile.
        static string? UsersEnvironment(string name)
        {
            if (name == "GIT_CONFIG_COUNT")
            {
                return null;
            }

            return System.Environment.GetEnvironmentVariable(name);
        }
    }

    static GitConfig Read(string repo, Func<string, string?> environment)
    {
        var gitDir = Path.Combine(repo, ".git");
        return GitConfig.Read(gitDir, gitDir, "main", environment);
    }

    // Git found on the path as Windows finds it: in its cmd or bin, or in mingw64's bin, quoted
    // or not, nothing that isn't a folder of git taken for one.
    [Test]
    public async Task FindsTheGitInstalled()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        foreach (var folder in new[] { "Cmd/cmd", "Mingw/mingw64/bin", "Ucrt/ucrt64/bin", "Usr/usr/bin", "Bin/bin" })
        {
            Directory.CreateDirectory(scratch.At(folder));
            await File.WriteAllTextAsync(Path.Combine(scratch.At(folder), "git.exe"), "");
        }

        Directory.CreateDirectory(scratch.At("Empty"));
        var separator = Path.PathSeparator;

        await Assert.That(GitConfig.FindSystemDirectory($"{scratch.At("Empty")}{separator}\"{scratch.At("Cmd", "cmd")}\"")).IsEqualTo(scratch.At("Cmd", "etc"));
        await Assert.That(GitConfig.FindSystemDirectory($"{separator}{separator}  {separator}{scratch.At("Mingw", "mingw64", "bin")}\\")).IsEqualTo(scratch.At("Mingw", "etc"));
        await Assert.That(GitConfig.FindSystemDirectory($"bad|folder{separator}{scratch.At("Usr", "usr", "bin")}")).IsEqualTo(scratch.At("Usr", "etc"));
        await Assert.That(GitConfig.FindSystemDirectory(scratch.At("Ucrt", "ucrt64", "bin"))).IsEqualTo(scratch.At("Ucrt", "etc"));
        await Assert.That(GitConfig.FindSystemDirectory(scratch.At("Bin", "bin"))).IsEqualTo(scratch.At("Bin", "etc"));
        await Assert.That(GitConfig.FindSystemDirectory(scratch.At("Empty"))).IsNull();
        await Assert.That(GitConfig.FindSystemDirectory("")).IsNull();
    }

    // The system's files where git var says the git on the path reads them, not the etc beside
    // that git.exe, which for Scoop's is a shim in scoop\shims running the git in scoop\apps,
    // whose etc has core.autocrlf, core.symlinks and the LFS filter. Where git can't say, not
    // run or older than 2.42 or giving a path in its own MSYS2 root, /etc/gitconfig, the etc
    // beside the git on the path, as Git for Windows lays it out.
    [Test]
    public async Task FindsTheSystemFilesWhereGitSays()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var shims = scratch.At("scoop", "shims");
        var cmd = scratch.At("Git", "cmd");
        foreach (var folder in new[] { shims, cmd })
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "git.exe"), "");
        }

        Directory.CreateDirectory(scratch.At("Empty"));
        var etc = scratch.At("scoop", "apps", "git", "current", "etc");
        var answers = new Dictionary<string, string>
        {
            ["GIT_CONFIG_SYSTEM"] = Path.Combine(etc, "gitconfig").Replace('\\', '/') + "\n",
            ["GIT_ATTR_SYSTEM"] = Path.Combine(etc, "gitattributes").Replace('\\', '/') + "\n"
        };
        var beside = (scratch.At("Git", "etc", "gitconfig"), scratch.At("Git", "etc", "gitattributes"));

        await Assert.That(GitConfig.FindSystemFiles(answers.GetValueOrDefault, shims)).IsEqualTo((Path.Combine(etc, "gitconfig"), Path.Combine(etc, "gitattributes")));
        await Assert.That(GitConfig.FindSystemFiles(_ => null, cmd)).IsEqualTo(beside);
        await Assert.That(GitConfig.FindSystemFiles(_ => "/etc/gitconfig\n", cmd)).IsEqualTo(beside);
        await Assert.That(GitConfig.FindSystemFiles(_ => "", cmd)).IsEqualTo(beside);
        await Assert.That(GitConfig.FindSystemFiles(_ => null, scratch.At("Empty"))).IsEqualTo(((string?)null, (string?)null));
    }

    // The system's config is the one the git on the path reads, as git config --system says,
    // whichever of its folders the path has first, or a shim; its gitattributes beside it, as
    // Git for Windows keeps them.
    [Test]
    public async Task FindsTheSystemConfigGitReads()
    {
        var listed = GitRun.Run(Path.GetTempPath(), "config", "--system", "--list", "--show-origin");
        var origin = listed.Output.Split('\n').FirstOrDefault(_ => _.StartsWith("file:", StringComparison.Ordinal));
        if (origin == null)
        {
            return;
        }

        var file = Path.GetFullPath(origin["file:".Length..origin.IndexOf('\t')]);

        await Assert.That(GitConfig.SystemConfig).IsEqualTo(file);
        await Assert.That(GitConfig.SystemAttributes).IsEqualTo(Path.Combine(Path.GetDirectoryName(file)!, "gitattributes"));
    }

    // ~ is the home folder, by itself or before a slash either way; nothing else is expanded.
    [Test]
    public async Task ExpandsTheHomeFolder()
    {
        var home = GitConfig.Home;

        await Assert.That(GitConfig.ExpandPath("~")).IsEqualTo(home);
        await Assert.That(GitConfig.ExpandPath("~/a/b")).IsEqualTo(Path.Combine(home, "a/b"));
        await Assert.That(GitConfig.ExpandPath(@"~\a")).IsEqualTo(Path.Combine(home, "a"));
        await Assert.That(GitConfig.ExpandPath("~user/a")).IsEqualTo("~user/a");
        await Assert.That(GitConfig.ExpandPath("a/~")).IsEqualTo("a/~");
    }
}
