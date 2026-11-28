/// <summary>
/// Git pull, as git pull does it, but in two parts: the fetch, which Cancel stops where it is,
/// as stopped part way it has changed nothing checked out, then the merge or rebase of what it
/// fetched, which, once begun, is left to its end, as any git changing the repository is: see
/// <see cref="CodeCommands.GitToItsEnd(string, Cancel, string[])"/>. Run as one git pull, Cancel
/// stopped the merge or rebase with the fetch, part way, which could leave the index locked, or
/// a rebase part way with its lock, and the undo's abort then failed.
/// Which of them, and how, is as git pull picks, from the same config: branch.&lt;name&gt;.rebase,
/// or else pull.rebase, for whether to rebase, merges keeping the branch's merges, and
/// interactive asking an editor for the commits, which fails at once; pull.ff for whether to
/// fast-forward only, never or where it can, only winning over a rebase the config asks for; and
/// rebase.autoStash for whether a rebase sets aside what is uncommitted, or is refused with it.
/// With neither rebase nor pull.ff set, a branch that has diverged is refused. What it says is
/// what git pull would say, but for the fetch's part coming first.
/// </summary>
static class GitPull
{
    // How branch.<name>.rebase or pull.rebase has it rebase.
    enum Rebasing
    {
        No,
        Yes,
        Merges,
        Interactive
    }

    // What the config says: how to rebase, and whether that was said at all; pull.ff as the merge
    // option it is, null where it isn't set; and rebase.autoStash.
    sealed record Settings(Rebasing Rebasing, bool Unspecified, string? FastForward, bool AutoStash);

    // What the repository was as the pull began: the branch, null when detached, the commit,
    // null before the first, and whether any file is in conflict, changed but not staged, or
    // staged but not committed.
    sealed record Before(string? Branch, string? Commit, bool Unmerged, bool Unstaged, bool Staged);

    /// <param name="environment">Variables git is run with, and its config read with, as
    /// <see cref="CodeCommands.GitToItsEnd(string, Cancel, IReadOnlyDictionary{string, string}?, string[])"/>
    /// takes them: GIT_CONFIG_NOSYSTEM say, for a test to pull with no system config.</param>
    public static CodeCommands.RepoResult Run(string repo, Cancel cancel, IReadOnlyDictionary<string, string>? environment = null)
    {
        // Submodules ignored, as git pull ignores them in asking whether anything is uncommitted.
        var status = CodeCommands.GitOutput(repo, cancel, ["--no-optional-locks", "status", "--porcelain=v2", "--branch", "--untracked-files=no", "--ignore-submodules=all", "-z"]);
        if (!status.Succeeded)
        {
            return new(repo, false, $"{status.Output}\n{status.Error}".Trim());
        }

        var before = Parse(status.Output);

        // In the order git pull reads and checks them, all before it fetches.
        if (Read(ConfigOf(repo, before.Branch, environment), before.Branch, out var settings) is { } invalid)
        {
            return new(repo, false, invalid);
        }

        if (before.Unmerged)
        {
            return new(repo, false, unmerged);
        }

        string? forkPoint = null;
        if (settings.Rebasing != Rebasing.No)
        {
            if (!settings.AutoStash &&
                WhyNotClean(before) is { } dirty)
            {
                return new(repo, false, dirty);
            }

            forkPoint = ForkPoint(repo);
        }

        // As git pull fetches: from the branch's remote, or origin, writing what it fetched, the
        // branch tracked among it, to FETCH_HEAD, whatever fetch.writeFetchHead says.
        var fetch = CodeCommands.Git(repo, cancel, network: true, null, ["fetch", "--update-head-ok", "--write-fetch-head"], environment);
        if (!fetch.Succeeded)
        {
            return fetch;
        }

        var brought = BringIn(repo, before, settings, forkPoint, AsPull(environment), cancel);
        return brought with
        {
            Output = $"{fetch.Output}\n{brought.Output}".Trim()
        };
    }

    // The merge or rebase of what was fetched, as git pull picks it.
    static CodeCommands.RepoResult BringIn(string repo, Before before, Settings settings, string? forkPoint, IReadOnlyDictionary<string, string> environment, Cancel cancel)
    {
        var rebasing = settings.Rebasing;
        var heads = MergeHeads(repo);
        if (heads.Count == 0)
        {
            return new(repo, false, NoMergeCandidates(repo, before.Branch, rebasing != Rebasing.No));
        }

        // Passed on only where a rebase was asked for, to whichever runs, as git pull passes it.
        string? autoStash = null;
        if (rebasing != Rebasing.No)
        {
            autoStash = settings.AutoStash ? "--autostash" : "--no-autostash";
        }

        // Before the first commit, the branch is made at what was fetched.
        if (before.Commit is not { } head)
        {
            if (heads.Count > 1)
            {
                return new(repo, false, "fatal: Cannot merge multiple branches into empty head.");
            }

            return Merge(repo, cancel, environment, null, null);
        }

        var fastForward = settings.FastForward;
        if (heads.Count > 1)
        {
            if (rebasing != Rebasing.No)
            {
                return new(repo, false, "fatal: Cannot rebase onto multiple branches.");
            }

            if (fastForward == "--ff-only")
            {
                return new(repo, false, "fatal: Cannot fast-forward to multiple branches.");
            }
        }

        var (canFastForward, upToDate) = Apart(repo, head, heads);
        var diverged = !canFastForward &&
                       !upToDate;

        // Fast-forward only wins over a rebase. Diverged, the merge says it can't, changing
        // nothing, as git pull says it.
        if (fastForward == "--ff-only")
        {
            rebasing = Rebasing.No;
        }
        else if (fastForward == null &&
                 settings.Unspecified &&
                 diverged)
        {
            return new(repo, false, needToSpecify);
        }

        if (rebasing == Rebasing.No)
        {
            return Merge(repo, cancel, environment, fastForward, autoStash);
        }

        // Nothing of the branch's own to rebase: git pull fast-forwards it.
        if (canFastForward)
        {
            return Merge(repo, cancel, environment, "--ff-only", autoStash);
        }

        return Rebase(repo, cancel, environment, rebasing, autoStash, head, heads[0], forkPoint);
    }

    // Merged as git pull merges, FETCH_HEAD naming the commit, so the message is git pull's too:
    // Merge branch 'main' of its url.
    static CodeCommands.RepoResult Merge(string repo, Cancel cancel, IReadOnlyDictionary<string, string> environment, string? fastForward, string? autoStash)
    {
        List<string> arguments = ["merge"];
        if (fastForward != null)
        {
            arguments.Add(fastForward);
        }

        if (autoStash != null)
        {
            arguments.Add(autoStash);
        }

        arguments.Add("FETCH_HEAD");
        return CodeCommands.GitToItsEnd(repo, cancel, environment, [.. arguments]);
    }

    // Rebased as git pull rebases: onto what was fetched, from where the branch and it last met,
    // as far back as where the branch tracked was before the fetch, should that have been
    // rewritten since, a force push say.
    static CodeCommands.RepoResult Rebase(string repo, Cancel cancel, IReadOnlyDictionary<string, string> environment, Rebasing rebasing, string? autoStash, string head, string mergeHead, string? forkPoint)
    {
        List<string> bases = ["merge-base", "--octopus", head, mergeHead];
        if (forkPoint != null)
        {
            bases.Add(forkPoint);
        }

        var found = CodeCommands.GitOutput(repo, Cancel.None, [.. bases]);
        var upstream = found.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        // Without one, git pull gives rebase no commit, which rebase refuses.
        upstream ??= new('0', mergeHead.Length);
        List<string> arguments = ["rebase"];
        if (rebasing == Rebasing.Merges)
        {
            arguments.Add("--rebase-merges");
        }
        else if (rebasing == Rebasing.Interactive)
        {
            arguments.Add("--interactive");
        }

        if (autoStash != null)
        {
            arguments.Add(autoStash);
        }

        arguments.AddRange(["--onto", mergeHead, upstream]);
        return CodeCommands.GitToItsEnd(repo, cancel, environment, [.. arguments]);
    }

    // The environment given, with the reflog saying pull, as of git pull's merge or rebase,
    // unless the environment Lode was started with says otherwise, as git pull leaves it then.
    static Dictionary<string, string> AsPull(IReadOnlyDictionary<string, string>? environment)
    {
        var asPull = new Dictionary<string, string>
        {
            ["GIT_REFLOG_ACTION"] = Environment.GetEnvironmentVariable("GIT_REFLOG_ACTION") ?? "pull"
        };
        if (environment != null)
        {
            foreach (var (name, value) in environment)
            {
                asPull[name] = value;
            }
        }

        return asPull;
    }

    // Whether what was fetched can be fast-forwarded to, having all the branch has, and whether
    // the branch has all of it already. One that has neither has diverged. Of several, as
    // branch.<name>.merge can name, none is fast-forwarded to.
    static (bool CanFastForward, bool UpToDate) Apart(string repo, string head, IReadOnlyList<string> heads)
    {
        if (heads.Count > 1)
        {
            var all = heads.All(_ => CodeCommands.GitOutput(repo, Cancel.None, ["merge-base", "--is-ancestor", _, head]).Succeeded);
            return (false, all);
        }

        // "ahead<tab>behind".
        var counts = CodeCommands.GitOutput(repo, Cancel.None, ["rev-list", "--left-right", "--count", $"{head}...{heads[0]}"]);
        var parts = counts.Output.Split('\t', StringSplitOptions.TrimEntries);
        if (!counts.Succeeded ||
            parts.Length != 2 ||
            !int.TryParse(parts[0], out var ahead) ||
            !int.TryParse(parts[1], out var behind))
        {
            return (false, false);
        }

        return (ahead == 0, behind == 0);
    }

    // The commits FETCH_HEAD has to merge: those of branch.<name>.merge, as the fetch marks
    // them, the rest not-for-merge. Each line is the commit, a tab, the mark or nothing, a tab
    // and what it is.
    static List<string> MergeHeads(string repo)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is not { } git)
            {
                return [];
            }

            return File.ReadAllLines(Path.Combine(git, "FETCH_HEAD"))
                .Select(_ => _.Split('\t'))
                .Where(_ => _.Length >= 2 && _[1].Length == 0)
                .Select(_ => _[0])
                .ToList();
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            return [];
        }
    }

    // Where the branch tracked was when the branch last took from it, as its reflog has it,
    // read before the fetch moves it, as git pull reads it: a rebase from there leaves out
    // commits a force push took off it. Null where it can't be told.
    static string? ForkPoint(string repo)
    {
        var found = CodeCommands.GitOutput(repo, Cancel.None, ["merge-base", "--fork-point", "@{upstream}", "HEAD"]);
        var commit = found.Output.Trim();
        if (!found.Succeeded ||
            commit.Length == 0)
        {
            return null;
        }

        return commit;
    }

    // What git pull says where the fetch gave it nothing to merge: no branch checked out, none
    // tracked, or the one tracked not on the remote.
    static string NoMergeCandidates(string repo, string? branch, bool rebasing)
    {
        var which = "Please specify which branch you want to merge with.";
        if (rebasing)
        {
            which = "Please specify which branch you want to rebase against.";
        }

        var specify = $"{which}\nSee git-pull(1) for details.\n\n    git pull <remote> <branch>";
        if (branch == null)
        {
            return $"You are not currently on a branch.\n{specify}";
        }

        var merge = CodeCommands.Git(repo, "config", "--get", $"branch.{branch}.merge");
        if (merge.Succeeded &&
            merge.Output.Length > 0)
        {
            return $"Your configuration specifies to merge with the ref '{merge.Output}'\nfrom the remote, but no such ref was fetched.";
        }

        // The remote named where there is only the one.
        var remote = "<remote>";
        var remotes = CodeCommands.GitOutput(repo, Cancel.None, ["remote"]).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remotes is [var only])
        {
            remote = only;
        }

        return $"There is no tracking information for the current branch.\n{specify}\n\nIf you wish to set tracking information for this branch you can do so with:\n\n    git branch --set-upstream-to={remote}/<branch> {branch}";
    }

    const string unmerged =
        """
        error: Pulling is not possible because you have unmerged files.
        hint: Fix them up in the work tree, and then use 'git add/rm <file>'
        hint: as appropriate to mark resolution and make a commit.
        fatal: Exiting because of an unresolved conflict.
        """;

    const string needToSpecify =
        """
        hint: You have divergent branches and need to specify how to reconcile them.
        hint: You can do so by running one of the following commands sometime before
        hint: your next pull:
        hint:
        hint:   git config pull.rebase false  # merge
        hint:   git config pull.rebase true   # rebase
        hint:   git config pull.ff only       # fast-forward only
        hint:
        hint: You can replace "git config" with "git config --global" to set a default
        hint: preference for all repositories. You can also pass --rebase, --no-rebase,
        hint: or --ff-only on the command line to override the configured default per
        hint: invocation.
        fatal: Need to specify how to reconcile divergent branches.
        """;

    // Why a rebase that doesn't set aside what is uncommitted is refused, as git pull refuses
    // it: files changed and not staged, or staged and not committed. Files git doesn't track
    // aren't in the way.
    static string? WhyNotClean(Before before)
    {
        if (before.Unstaged)
        {
            var why = "error: cannot pull with rebase: You have unstaged changes.";
            if (before.Staged)
            {
                why += "\nerror: additionally, your index contains uncommitted changes.";
            }

            return $"{why}\nerror: Please commit or stash them.";
        }

        if (before.Staged)
        {
            return "error: cannot pull with rebase: Your index contains uncommitted changes.\nerror: Please commit or stash them.";
        }

        return null;
    }

    // How to rebase, fast-forward and set aside, as git pull reads them, or why it would refuse
    // to pull: a value it doesn't know. Each value is as the config has it, "true" for a key set
    // without one.
    static string? Read(Func<string, string?> config, string? branch, out Settings settings)
    {
        settings = new(Rebasing.No, true, null, false);
        var autoStash = false;
        if (config("rebase.autostash") is { } autoStashValue)
        {
            if (GitConfig.ParseBool(autoStashValue) is not { } parsed)
            {
                return $"fatal: bad boolean config value '{autoStashValue}' for 'rebase.autostash'";
            }

            autoStash = parsed;
        }

        string? fastForward = null;
        if (config("pull.ff") is { } fastForwardValue)
        {
            fastForward = GitConfig.ParseBool(fastForwardValue) switch
            {
                true => "--ff",
                false => "--no-ff",
                null when fastForwardValue == "only" => "--ff-only",
                _ => null
            };
            if (fastForward == null)
            {
                return $"fatal: invalid value for 'pull.ff': '{fastForwardValue}'";
            }
        }

        // The branch's own first, then pull's.
        var key = "pull.rebase";
        var value = config(key);
        if (branch != null &&
            config($"branch.{branch}.rebase") is { } own)
        {
            key = $"branch.{branch}.rebase";
            value = own;
        }

        var rebasing = Rebasing.No;
        if (value != null)
        {
            if (RebasingOf(value) is not { } parsedRebasing)
            {
                var invalid = $"fatal: invalid value for '{key}': '{value}'";
                if (value is "preserve" or "p")
                {
                    return $"error: {value}: 'preserve' superseded by 'merges'\n{invalid}";
                }

                return invalid;
            }

            rebasing = parsedRebasing;
        }

        settings = new(rebasing, value == null, fastForward, autoStash);
        return null;
    }

    // As git's rebase_parse_value: a boolean, or merges or interactive, each by its first letter
    // too. Null for any other.
    static Rebasing? RebasingOf(string value) =>
        GitConfig.ParseBool(value) switch
        {
            true => Rebasing.Yes,
            false => Rebasing.No,
            _ => value switch
            {
                "merges" or "m" => Rebasing.Merges,
                "interactive" or "i" => Rebasing.Interactive,
                _ => null
            }
        };

    /// <summary>
    /// The repository's config, as git reads it: read in process, as Lode reads it for the git
    /// state, or, where that can't say as git would, an include on a condition only git can
    /// tell say, asked of git. Each value as the config has it, "true" for a key set without
    /// one, null for one not set. With <paramref name="environment"/>, as git run with it would
    /// read it.
    /// </summary>
    internal static Func<string, string?> ConfigOf(string repo, string? branch, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            if (CodeCommands.GitDirectory(repo) is { } git)
            {
                var config = GitConfig.Read(
                    git,
                    CodeCommands.CommonDirectory(git),
                    branch,
                    _ => environment?.GetValueOrDefault(_) ?? Environment.GetEnvironmentVariable(_));
                if (!config.Uncertain)
                {
                    return _ => config.IsSet(_) ? config.Get(_) ?? "true" : null;
                }
            }
        }
        catch (Exception exception)
            when (
                exception is
                    GitFilesException or
                    IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
        {
            // Asked of git instead.
        }

        // Booleans as true and false, as git takes them, and any other value as it is.
        return _ =>
        {
            var read = CodeCommands.Git(repo, Cancel.None, network: false, null, ["config", "--type=bool-or-str", "--get", _], environment);
            if (!read.Succeeded)
            {
                return null;
            }

            return read.Output;
        };
    }

    // What git status --porcelain=v2 --branch -z said: headers, then an entry for each path
    // changed, "1" or "2" with what is staged and what isn't, "." for nothing, and "u" for one
    // in conflict. A "2" has where it was renamed from as the next entry.
    static Before Parse(string output)
    {
        string? branch = null;
        string? commit = null;
        var unmergedFiles = false;
        var unstaged = false;
        var staged = false;
        var entries = output.Split('\0');
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var oid = entry["# branch.oid ".Length..];
                if (oid != "(initial)")
                {
                    commit = oid;
                }

                continue;
            }

            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var name = entry["# branch.head ".Length..];
                if (name != "(detached)")
                {
                    branch = name;
                }

                continue;
            }

            if (entry.StartsWith("u ", StringComparison.Ordinal))
            {
                unmergedFiles = true;
                continue;
            }

            if (entry.Length < 4 ||
                entry[0] is not ('1' or '2') ||
                entry[1] != ' ')
            {
                continue;
            }

            staged |= entry[2] != '.';
            unstaged |= entry[3] != '.';
            if (entry[0] == '2')
            {
                index++;
            }
        }

        return new(branch, commit, unmergedFiles, unstaged, staged);
    }
}
