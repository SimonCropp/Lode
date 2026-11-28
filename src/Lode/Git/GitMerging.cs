/// <summary>
/// Bringing another branch into the one checked out, as GitHub Desktop's Branch menu does:
/// merging it, squashed into one commit or not, rebasing onto it, and bringing in the default
/// branch. A merge that conflicts is left part way, for the conflicts to be settled in the
/// Changes pane and the merge committed, or given up there. A squash, rebase or update from the
/// default branch that conflicts is undone, saying which files, as Changes has no squash or
/// rebase under way to settle. Each refuses with changes to tracked files, which giving it up
/// could take with it. Nothing is pushed, but for a rebase asked to force push.
/// And the other way: the branch checked out merged into another without checking that out, so
/// it, and the folder, stay as they are, pushed after where asked.
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

        // Commits origin has that the branch hasn't would go with a push forced, so none is
        // offered. Counted past them, origin's would be taken for commits it rewrites.
        if (Unpulled(repo, cancel) is int unpulled and > 0)
        {
            return new($"{text}\n\n{NotPulled(unpulled, current)}, so force pushing afterwards isn't offered: pull first, then rebase.", true);
        }

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

    // How many commits the branch checked out hasn't of those origin has, where a push would
    // go, as last fetched: pushed by someone else since it was, fetched, but not pulled. Null
    // where git can't say, with nowhere to push to, say.
    static int? Unpulled(string repo, Cancel cancel)
    {
        var count = CodeCommands.GitOutput(repo, cancel, ["rev-list", "--count", "HEAD..@{push}"]);
        if (count.Succeeded &&
            int.TryParse(count.Output.Trim(), out var unpulled))
        {
            return unpulled;
        }

        return null;
    }

    static string NotPulled(int unpulled, string branch) =>
        $"Origin has {GitState.CountOf(unpulled, "commit")} on {branch} not pulled yet, which a force push would delete";

    static string FirstLine(string text) =>
        text.Split('\n', 2)[0].Trim();

    /// <summary>
    /// A branch the branch checked out is merged into: a local one, or one only on a remote,
    /// origin/master say, which gets a local branch of its name, <paramref name="Name"/>,
    /// tracking it. <paramref name="Ref"/> is what is read for it, <paramref name="Tip"/> the
    /// commit it is at, and <paramref name="Remote"/> the remote branch, where it is one.
    /// </summary>
    internal sealed record Target(string Name, string Ref, string Tip, string? Remote);

    // The branch picked, as a local branch where there is one of the name, otherwise as a
    // remote's. Null where it is neither.
    static Target? TargetOf(string repo, string picked, Cancel cancel)
    {
        if (TipOf(repo, $"refs/heads/{picked}", cancel) is { } local)
        {
            return new(picked, $"refs/heads/{picked}", local, null);
        }

        var slash = picked.IndexOf('/');
        if (slash > 0 &&
            TipOf(repo, $"refs/remotes/{picked}", cancel) is { } remote)
        {
            return new(picked[(slash + 1)..], $"refs/remotes/{picked}", remote, picked);
        }

        return null;
    }

    // The commit a ref is at, from what git writes to its output alone.
    static string? TipOf(string repo, string reference, Cancel cancel)
    {
        var tip = CodeCommands.GitOutput(repo, cancel, ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"]);
        if (tip.Succeeded &&
            tip.Output.Trim().Length > 0)
        {
            return tip.Output.Trim();
        }

        return null;
    }

    // Why the branch checked out can't be merged into the target from here, or null where it
    // can: a target only on a remote whose name a local branch has already, or one checked out
    // in another worktree, whose folder would no longer match it.
    static string? WhyNotInto(string repo, string current, Target target, Cancel cancel)
    {
        if (target.Remote != null &&
            TipOf(repo, $"refs/heads/{target.Name}", cancel) != null)
        {
            return $"There is a local {target.Name} already, not tracking {target.Remote}: merge {current} into that instead.";
        }

        if (target.Remote == null &&
            CheckedOutAt(repo, target.Ref, cancel) is { } folder)
        {
            return $"{target.Name} is checked out in {folder}, so it can't be changed from here: merge {current} into it there.";
        }

        return null;
    }

    // The folder of the worktree with the branch checked out, or null where none has.
    static string? CheckedOutAt(string repo, string reference, Cancel cancel)
    {
        var list = CodeCommands.Git(repo, cancel, network: false, "worktree", "list", "--porcelain");
        if (!list.Succeeded)
        {
            return null;
        }

        string? folder = null;
        foreach (var line in list.Output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                folder = line["worktree ".Length..];
            }
            else if (line == $"branch {reference}")
            {
                return Path.GetFullPath(folder ?? repo);
            }
        }

        return null;
    }

    // "target-only<tab>current-only": the commits the target has that the branch checked out
    // hasn't, then those it has that the target hasn't. Null where git couldn't count them.
    static (int TargetOnly, int CurrentOnly)? Apart(string repo, Target target, Cancel cancel)
    {
        var counts = CodeCommands.Git(repo, cancel, network: false, "rev-list", "--left-right", "--count", $"{target.Tip}...HEAD");
        var parts = counts.Output.Split('\t', StringSplitOptions.TrimEntries);
        if (counts.Succeeded &&
            parts.Length == 2 &&
            int.TryParse(parts[0], out var targetOnly) &&
            int.TryParse(parts[1], out var currentOnly))
        {
            return (targetOnly, currentOnly);
        }

        return null;
    }

    /// <summary>
    /// What merging the branch checked out, <paramref name="current"/>, into
    /// <paramref name="picked"/> would do, leaving <paramref name="current"/> checked out: fast
    /// forward it, where it has nothing the branch checked out hasn't, otherwise merge, with a
    /// merge commit. One that would conflict can't go, as the conflicts could only be settled
    /// with the other branch checked out.
    /// </summary>
    public static Preview IntoOf(string repo, string current, string picked, Cancel cancel) =>
        CheckInto(repo, current, picked, cancel).Preview;

    /// <summary>
    /// What merging into another branch would come to in a repository: moved up to the branch
    /// checked out, a merge commit, nothing as it has it all already, a conflict, or not from
    /// here at all.
    /// </summary>
    internal enum IntoKind
    {
        FastForward,
        Merge,
        UpToDate,
        Conflict,
        Cannot
    }

    /// <summary>
    /// <see cref="IntoOf"/>, with which <see cref="IntoKind"/> it is, for several
    /// repositories' to be told together.
    /// </summary>
    internal sealed record IntoCheck(IntoKind Kind, Preview Preview);

    internal static IntoCheck CheckInto(string repo, string current, string picked, Cancel cancel)
    {
        if (TargetOf(repo, picked, cancel) is not { } target)
        {
            return new(IntoKind.Cannot, new($"Couldn't find {picked}.", false));
        }

        if (WhyNotInto(repo, current, target, cancel) is { } why)
        {
            return new(IntoKind.Cannot, new(why, false));
        }

        if (Apart(repo, target, cancel) is not var (targetOnly, currentOnly))
        {
            return new(IntoKind.Cannot, new($"Couldn't compare {current} with {picked}.", false));
        }

        if (currentOnly == 0)
        {
            return new(IntoKind.UpToDate, new($"{picked} already has every commit on {current}.", false));
        }

        var made = "";
        if (target.Remote != null)
        {
            made = $"\n\nA local {target.Name} is made for it, tracking {target.Remote}.";
        }

        if (targetOnly == 0)
        {
            return new(IntoKind.FastForward, new($"This will fast-forward {target.Name} by {GitState.CountOf(currentOnly, "commit")} to match {current}, leaving {current} checked out.{made}", true));
        }

        var tree = CodeCommands.GitOutput(repo, cancel, ["merge-tree", "--write-tree", "--name-only", "--no-messages", target.Tip, "HEAD"]);
        if (tree.ExitCode == 1)
        {
            var conflicted = Conflicted(tree.Output);
            return new(IntoKind.Conflict, new($"There will be {GitState.CountOf(conflicted.Count, "conflicted file")} when merging {current} into {target.Name}, which can only be settled with {target.Name} checked out: switch to it, and merge {current} into it there.\n{string.Join('\n', conflicted)}", false));
        }

        if (!tree.Succeeded)
        {
            return new(IntoKind.Cannot, new($"Couldn't merge {current} into {target.Name}: {FirstLine(tree.Error)}", false));
        }

        return new(IntoKind.Merge, new($"This will merge {GitState.CountOf(currentOnly, "commit")} from {current} into {target.Name}, with a merge commit, leaving {current} checked out.{made}", true));
    }

    // The files merge-tree says are in conflict: after the tree it would make, one a line.
    static List<string> Conflicted(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Merges the branch checked out, <paramref name="current"/>, into <paramref name="picked"/>,
    /// without checking it out, so the folder, and anything uncommitted in it, stays as it is:
    /// fast-forwarded where it can be, otherwise with a merge commit made as git would merge
    /// them, then the branch moved to it, only if it is still where it was. One that would
    /// conflict changes nothing. A branch only on a remote gets a local branch, tracking it.
    /// With <paramref name="push"/>, the branch is pushed after, to the remote it tracks, or
    /// origin, as it has no upstream then, which it is given.
    /// </summary>
    public static CodeCommands.RepoResult Into(string repo, string current, string picked, bool push, Cancel cancel)
    {
        if (TargetOf(repo, picked, cancel) is not { } target)
        {
            return new(repo, false, $"Couldn't find {picked}, so nothing was merged.");
        }

        if (WhyNotInto(repo, current, target, cancel) is { } why)
        {
            return new(repo, false, why);
        }

        var head = TipOf(repo, "HEAD", cancel);
        if (head == null ||
            Apart(repo, target, cancel) is not var (targetOnly, currentOnly))
        {
            return new(repo, false, $"Couldn't compare {current} with {picked}, so nothing was merged.");
        }

        if (currentOnly == 0)
        {
            return new(repo, true, $"{picked} already has every commit on {current}.");
        }

        string tip;
        string done;
        string reason;
        if (targetOnly == 0)
        {
            tip = head;
            done = $"Fast-forwarded {target.Name} by {GitState.CountOf(currentOnly, "commit")} to match {current}.";
            reason = $"merge {current}: Fast-forward";
        }
        else
        {
            var tree = CodeCommands.GitOutput(repo, cancel, ["merge-tree", "--write-tree", "--name-only", "--no-messages", target.Tip, head]);
            if (tree.ExitCode == 1)
            {
                return new(repo, false, $"Merging {current} into {target.Name} would conflict, so nothing was merged: switch to {target.Name}, and merge {current} into it there.\nIn conflict:\n{string.Join('\n', Conflicted(tree.Output))}");
            }

            if (!tree.Succeeded)
            {
                return new(repo, false, $"Couldn't merge {current} into {target.Name}, so nothing was merged:\n{tree.Error}");
            }

            // The new commit's id is all git writes to its output: signing it, as commit.gpgSign
            // may have it, writes to the errors.
            var commit = CodeCommands.GitOutput(
                repo,
                cancel,
                [
                    "commit-tree",
                    FirstLine(tree.Output),
                    "-p",
                    target.Tip,
                    "-p",
                    head,
                    "-m",
                    $"Merge branch '{current}' into {target.Name}"
                ]);
            if (!commit.Succeeded)
            {
                return new(repo, false, $"Couldn't make the merge commit, so nothing was merged:\n{commit.Error}");
            }

            tip = commit.Output.Trim();
            done = $"Merged {GitState.CountOf(currentOnly, "commit")} from {current} into {target.Name}.";
            reason = $"merge {current}: Merge made by Lode";
        }

        // Moved only from where it was read, or, for a new local branch, only if there still is
        // none: anything else moving it since, it is left as that left it.
        var old = target.Tip;
        if (target.Remote != null)
        {
            old = "";
        }

        var moved = CodeCommands.GitToItsEnd(repo, cancel, "update-ref", "-m", reason, $"refs/heads/{target.Name}", tip, old);
        if (!moved.Succeeded)
        {
            return moved with
            {
                Output = $"Couldn't move {target.Name}, so nothing was merged:\n{moved.Output}"
            };
        }

        var said = new List<string> { done };
        if (target.Remote != null)
        {
            var tracked = CodeCommands.Git(repo, cancel, network: false, "branch", $"--set-upstream-to={target.Remote}", target.Name);
            if (tracked.Succeeded)
            {
                said.Add($"Made a local {target.Name}, tracking {target.Remote}.");
            }
            else
            {
                said.Add($"Made a local {target.Name}, but couldn't have it track {target.Remote}:\n{tracked.Output}");
            }
        }

        if (!push)
        {
            return new(repo, true, string.Join('\n', said));
        }

        var pushed = PushBranch(repo, target.Name, cancel);
        said.Add(pushed.Output);
        return pushed with
        {
            Output = string.Join('\n', said).Trim()
        };
    }

    // Pushes a branch not checked out to the branch it tracks, or, with none, to origin, under
    // its own name, which it then tracks.
    static CodeCommands.RepoResult PushBranch(string repo, string branch, Cancel cancel)
    {
        var remote = CodeCommands.Git(repo, cancel, network: false, "config", "--get", $"branch.{branch}.remote");
        var merge = CodeCommands.Git(repo, cancel, network: false, "config", "--get", $"branch.{branch}.merge");
        if (remote is { Succeeded: true, Output.Length: > 0 } &&
            merge is { Succeeded: true, Output.Length: > 0 })
        {
            return CodeCommands.Git(repo, cancel, network: true, "push", remote.Output.Trim(), $"refs/heads/{branch}:{merge.Output.Trim()}");
        }

        return CodeCommands.Git(repo, cancel, network: true, "push", "--set-upstream", "origin", $"refs/heads/{branch}:refs/heads/{branch}");
    }

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
            var conflicted = CodeCommands.ConflictedFiles(repo);
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
        var conflicted = CodeCommands.ConflictedFiles(repo);
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
    /// <paramref name="forcePush"/>, pushed after, overwriting origin's, though only where the
    /// branch had every commit origin has, so nothing pushed by anyone else is lost: not where
    /// origin has moved on since it was last fetched, nor where it was fetched but not pulled,
    /// which is refused before rebasing, nor fetched while rebasing, which the push refuses.
    /// </summary>
    public static CodeCommands.RepoResult Rebase(string repo, string onto, bool forcePush, Cancel cancel)
    {
        if (WhyNot(repo, "nothing was rebased") is { } why)
        {
            return new(repo, false, why);
        }

        // A lease alone, as last fetched, would pass over commits fetched and not pulled.
        if (forcePush &&
            Unpulled(repo, cancel) is int unpulled and > 0)
        {
            var branch = CodeCommands.Git(repo, "symbolic-ref", "--quiet", "--short", "HEAD").Output;
            return new(repo, false, $"{NotPulled(unpulled, branch)}, so nothing was rebased: pull first, then rebase.");
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

        // The lease has origin's branch still where it was last fetched; the include check has
        // that in the branch's history before the rebase, which a fetch since might not be.
        var push = CodeCommands.Git(repo, cancel, network: true, "push", "--force-with-lease", "--force-if-includes");
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

        var conflicted = CodeCommands.ConflictedFiles(repo);
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
    // branch checked out, as Push won't commit then, a merge or rebase under way already, or
    // changes to tracked files, which undoing a merge or rebase that conflicted could take with
    // it. Files git doesn't track yet stay.
    static string? WhyNot(string repo, string notDone)
    {
        if (CodeCommands.WhyNotMerge(repo, notDone) is { } why)
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
