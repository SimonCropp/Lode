/// <summary>
/// Bringing another branch into the one checked out, as GitHub Desktop's Branch menu does:
/// merging it, squashed into one commit or not, rebasing onto it, and bringing in the default
/// branch. A merge that conflicts is left part way, for the conflicts to be settled in the
/// Changes pane and the merge committed, or given up there. A squash, rebase or update from the
/// default branch that conflicts is undone, saying which files, as Changes has no squash or
/// rebase under way to settle. Each refuses with changes to tracked files, which giving it up
/// could take with it. Nothing is pushed, but for a rebase asked to force push.
/// </summary>
static class GitMerging
{
    public enum Kind
    {
        Merge,
        Squash,
        Rebase
    }

    /// <summary>
    /// What the dialog says of the branch picked, as GitHub Desktop says it, and whether it can
    /// go ahead. <paramref name="OnOrigin"/>, for a rebase, is how many of the commits it
    /// rewrites origin has, for which a push afterwards would have to be forced.
    /// </summary>
    internal sealed record Preview(string Text, bool CanGo, int OnOrigin = 0);

    /// <summary>
    /// What <paramref name="kind"/> would do with <paramref name="branch"/> and the branch
    /// checked out, <paramref name="current"/>: how many commits, and, for a merge, whether any
    /// file would conflict, found as git would merge them, without touching the folder.
    /// </summary>
    public static Preview Of(string repo, string current, string branch, Kind kind, Cancel cancel)
    {
        // "ahead<tab>behind": the commits the branch checked out has that the other hasn't, then
        // those the other has.
        var counts = CodeCommands.Git(repo, cancel, network: false, "rev-list", "--left-right", "--count", $"HEAD...{branch}");
        var parts = counts.Output.Split('\t', StringSplitOptions.TrimEntries);
        if (!counts.Succeeded ||
            parts.Length != 2 ||
            !int.TryParse(parts[0], out var ahead) ||
            !int.TryParse(parts[1], out var behind))
        {
            return new($"Couldn't compare {current} with {branch}: {FirstLine(counts.Output)}", false);
        }

        if (behind == 0)
        {
            return new($"{current} is already up to date with {branch}.", false);
        }

        if (kind == Kind.Rebase)
        {
            return RebasePreview(repo, current, branch, ahead, behind, cancel);
        }

        var tree = CodeCommands.GitOutput(repo, cancel, ["merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", branch]);
        if (tree.ExitCode == 1)
        {
            // The tree it would make, then each file in conflict.
            var conflicted = tree.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(1)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var files = string.Join('\n', conflicted);
            var count = GitState.CountOf(conflicted.Count, "conflicted file");

            // A merge is left part way for them to be settled in Changes; a squash leaves no
            // merge under way there, so can't be.
            if (kind == Kind.Squash)
            {
                return new($"There will be {count} when squashing {branch} into {current}, so it can't be squashed here: merge it instead, settling them in Changes.\n{files}", false);
            }

            return new($"There will be {count} when merging {branch} into {current}, to settle in Changes before committing the merge.\n{files}", true);
        }

        if (!tree.Succeeded)
        {
            return new($"Couldn't merge {branch} into {current}: {FirstLine(tree.Error)}", false);
        }

        if (kind == Kind.Squash)
        {
            return new($"This will squash {GitState.CountOf(behind, "commit")} from {branch} into one commit on {current}.", true);
        }

        return new($"This will merge {GitState.CountOf(behind, "commit")} from {branch} into {current}.", true);
    }

    static Preview RebasePreview(string repo, string current, string branch, int ahead, int behind, Cancel cancel)
    {
        if (ahead == 0)
        {
            return new($"This will fast-forward {current} by {GitState.CountOf(behind, "commit")} to match {branch}.", true);
        }

        var text = $"This will update {current} by applying its {GitState.CountOf(ahead, "commit")} on top of {branch}. Should one conflict, the rebase is undone.";

        // The commits rebased that origin has: the branch's own there, past where it meets the
        // other. Rewritten, origin's would have to be overwritten, by a push forced.
        var onOrigin = 0;
        var tracked = CodeCommands.Git(repo, cancel, network: false, "rev-list", "--count", $"{branch}..@{{upstream}}");
        if (tracked.Succeeded &&
            int.TryParse(tracked.Output, out var count))
        {
            onOrigin = count;
        }

        if (onOrigin > 0)
        {
            text += $"\n\nOrigin has {GitState.CountOf(onOrigin, "commit")} of {current} that this rewrites, so pushing it afterwards needs force, and anyone else's copy of {current} is left behind.";
        }

        return new(text, true, onOrigin);
    }

    static string FirstLine(string text) =>
        text.Split('\n', 2)[0].Trim();

    /// <summary>
    /// Merges <paramref name="branch"/> into the branch checked out, or with
    /// <paramref name="squash"/>, its commits as one commit, with git's message for it, listing
    /// them, as GitHub Desktop commits it.
    /// </summary>
    public static CodeCommands.RepoResult Merge(string repo, string branch, bool squash, Cancel cancel)
    {
        if (WhyNot(repo, "nothing was merged") is { } why)
        {
            return new(repo, false, why);
        }

        if (!squash)
        {
            var merge = CodeCommands.GitToItsEnd(repo, cancel, "merge", "--no-edit", branch);
            if (merge.Succeeded)
            {
                return merge;
            }

            // Stopped by conflicts, it is left part way, for them to be settled in Changes.
            var conflicted = CodeCommands.Git(repo, "diff", "--name-only", "--diff-filter=U").Output;
            if (conflicted.Length > 0)
            {
                var count = conflicted.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                return merge with
                {
                    Succeeded = false,
                    Output = $"Merging {branch} left {GitState.CountOf(count, "conflicted file")}, to settle in Changes: pick a side for each, or settle its markers in the editor, then commit the merge. Abort merge there puts the branch back.\nIn conflict:\n{conflicted}"
                };
            }

            return CodeCommands.UndoMerge(repo, branch, merge, pushing: false);
        }

        // A squash leaves no merge under way to abort, only what it staged, in conflict or not.
        var squashed = CodeCommands.GitToItsEnd(repo, cancel, "merge", "--squash", branch);
        if (!squashed.Succeeded)
        {
            return Unsquash(repo, branch, squashed);
        }

        var commit = CodeCommands.GitToItsEnd(repo, cancel, "commit", "--no-edit");
        if (!commit.Succeeded)
        {
            return Unsquash(repo, branch, commit);
        }

        return commit with
        {
            Output = $"{squashed.Output}\n{commit.Output}".Trim()
        };
    }

    // A squash that failed, by a conflict or a commit git couldn't make, is put back as it was:
    // nothing was changed but what it staged, as nothing tracked was changed before it.
    static CodeCommands.RepoResult Unsquash(string repo, string branch, CodeCommands.RepoResult failed)
    {
        var conflicted = CodeCommands.Git(repo, "diff", "--name-only", "--diff-filter=U").Output;
        var reset = CodeCommands.Git(repo, "reset", "--merge");
        var undone = "It was undone: merge it yourself, settling the conflicts.";
        if (!reset.Succeeded)
        {
            undone = $"It couldn't be undone: settle the conflicts, or git reset --merge.\n{reset.Output}";
        }

        if (conflicted.Length == 0)
        {
            return failed with
            {
                Succeeded = false,
                Output = $"Squashing {branch} stopped part way. {undone}\n{failed.Output}".Trim()
            };
        }

        return failed with
        {
            Succeeded = false,
            Output = $"Squashing {branch} would conflict. {undone}\nIn conflict:\n{conflicted}"
        };
    }

    /// <summary>
    /// Rebases the branch checked out onto <paramref name="onto"/>: its commits that
    /// <paramref name="onto"/> hasn't, applied on top of it. One that conflicts is undone. With
    /// <paramref name="forcePush"/>, pushed after, overwriting origin's, though only as it was
    /// last fetched, so nothing pushed since by anyone else is lost.
    /// </summary>
    public static CodeCommands.RepoResult Rebase(string repo, string onto, bool forcePush, Cancel cancel)
    {
        if (WhyNot(repo, "nothing was rebased") is { } why)
        {
            return new(repo, false, why);
        }

        var rebase = CodeCommands.GitToItsEnd(repo, cancel, "rebase", onto);
        if (!rebase.Succeeded)
        {
            return Unrebase(repo, onto, rebase);
        }

        if (!forcePush)
        {
            return rebase;
        }

        var push = CodeCommands.Git(repo, cancel, network: true, "push", "--force-with-lease");
        return push with
        {
            Output = $"{rebase.Output}\n{push.Output}".Trim()
        };
    }

    static CodeCommands.RepoResult Unrebase(string repo, string onto, CodeCommands.RepoResult failed)
    {
        if (!Rebasing(repo))
        {
            return failed with
            {
                Output = $"Couldn't rebase onto {onto}, so nothing was rebased:\n{failed.Output}".Trim()
            };
        }

        var conflicted = CodeCommands.Git(repo, "diff", "--name-only", "--diff-filter=U").Output;
        var abort = CodeCommands.Git(repo, "rebase", "--abort");
        var undone = "The rebase was undone: rebase it yourself, settling the conflicts.";
        if (!abort.Succeeded)
        {
            undone = $"The rebase couldn't be undone: settle the conflicts, or git rebase --abort.\n{abort.Output}";
        }

        if (conflicted.Length == 0)
        {
            return failed with
            {
                Succeeded = false,
                Output = $"Rebasing onto {onto} stopped part way. {undone}\n{failed.Output}".Trim()
            };
        }

        return failed with
        {
            Succeeded = false,
            Output = $"Rebasing onto {onto} would conflict. {undone}\nIn conflict:\n{conflicted}"
        };
    }

    // Whether git is part way through a rebase, which keeps its state in a folder of its own.
    static bool Rebasing(string repo)
    {
        foreach (var name in (string[]) ["rebase-merge", "rebase-apply"])
        {
            var path = CodeCommands.Git(repo, "rev-parse", "--git-path", name);
            if (path.Succeeded &&
                Directory.Exists(Path.Combine(repo, path.Output)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Brings the default branch into each repository's branch checked out, as GitHub Desktop's
    /// Update from default branch does: origin's, as fetched now, or for a fork, upstream's, the
    /// repository it was made from. The default branch itself is left to Pull. Nothing is pushed.
    /// </summary>
    public static Task<List<CodeCommands.RepoResult>> UpdateFromDefault(IReadOnlyList<string> repos, IProgress<string>? progress = null, Cancel cancel = default) =>
        CodeCommands.ForEach(repos, progress, cancel, repo => UpdateFromDefault(repo, cancel));

    static CodeCommands.RepoResult UpdateFromDefault(string repo, Cancel cancel)
    {
        if (WhyNot(repo, "nothing was merged") is { } why)
        {
            return new(repo, false, why);
        }

        var remote = "origin";
        if (CodeCommands.IsFork(repo))
        {
            remote = "upstream";
        }

        var fetch = CodeCommands.Git(repo, cancel, network: true, "fetch", remote);
        if (!fetch.Succeeded)
        {
            return fetch;
        }

        var parent = CodeCommands.DefaultBranch(repo, cancel, remote);
        if (!parent.Succeeded)
        {
            return parent;
        }

        var current = CodeCommands.Git(repo, "symbolic-ref", "--quiet", "--short", "HEAD").Output;
        if (remote == "origin" &&
            current == parent.Output)
        {
            return new(repo, true, $"{current} is the default branch: Pull brings it up to date.");
        }

        var from = $"{remote}/{parent.Output}";
        var merge = CodeCommands.GitToItsEnd(repo, cancel, "merge", "--no-edit", from);
        if (merge.Succeeded)
        {
            return merge;
        }

        return CodeCommands.UndoMerge(repo, from, merge, pushing: false);
    }

    // Why it is not to merge or rebase here, or null where it can: files in conflict, or no
    // branch checked out, as Push won't commit then, or changes to tracked files, which undoing a
    // merge or rebase that conflicted could take with it. Files git doesn't track yet stay.
    static string? WhyNot(string repo, string notDone)
    {
        if (CodeCommands.WhyNotChange(repo, notDone) is { } why)
        {
            return why;
        }

        var status = CodeCommands.Git(repo, "status", "--porcelain", "--untracked-files=no", "--ignore-submodules=dirty");
        if (status is
            {
                Succeeded: true,
                Output.Length: > 0
            })
        {
            return $"There are uncommitted changes, so {notDone}: commit or stash them first.\n{status.Output}";
        }

        return null;
    }
}
