using System.Text;

/// <summary>
/// Commits read in process as git log reads them, however they are written, git the oracle; and
/// the tag git describe chooses, and how far branches are apart, over histories of every shape.
/// </summary>
public class CommitsTests
{
    static List<string> Lines(List<GitCommit>? commits)
    {
        if (commits == null)
        {
            return ["none"];
        }

        return commits
            .Select(_ => $"{_.Sha} [{string.Join(' ', _.Parents)}] {_.Author} {_.When.ToUnixTimeSeconds()} [{string.Join(' ', _.Tags)}] {_.Subject}")
            .ToList();
    }

    // Commits as git hash-object writes them, one on another, HEAD's branch moved to the last.
    static void Chain(string repo, IEnumerable<string> bodies)
    {
        var tree = GitRun.Output(repo, "rev-parse", "HEAD^{tree}");
        var parent = GitRun.Output(repo, "rev-parse", "HEAD");
        foreach (var body in bodies)
        {
            parent = GitRun.WriteObject(repo, "commit", $"tree {tree}\nparent {parent}\n{body}");
        }

        GitRun.Output(repo, "update-ref", "refs/heads/main", parent);
    }

    // Authors without an email, without its closing bracket, with spaces before it, without a
    // date or with one that isn't a number; or no author at all, or no committer, or a header
    // before the author's or between it and the committer's, or no header but the tree and
    // parents; messages after blank lines, with spaces and CRLFs, without a blank line before
    // them, or none at all, or no line break at the end; signed, and in UTF-8 by name. With a
    // .mailmap, so each author's email is read too.
    [Test]
    public async Task ReadsCommitsAsGitLogDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, ".mailmap"), "Mapped <a@example.com>\n");
        Chain(
            repo,
            [
                "author No Email 1600000000 +0000\ncommitter C <c@example.com> 1600000000 +0000\n\nNo brackets\n",
                "author Open <bracket 1600000001 +0000\ncommitter C <c@example.com> 1600000001 +0000\n\nNo closing bracket\n",
                "author Spaces   <s@example.com> 1600000002 +0000\ncommitter C <c@example.com> 1600000002 +0000\n\n\n \n  Leading lines and spaces  \r\nsecond line\t\n\nThe body.\n",
                "author No Date <d@example.com>\ncommitter C <c@example.com> 1600000003 +0000\n\nNo date\n",
                "author Junk Date <j@example.com> notanumber +0000\ncommitter C <c@example.com> 1600000004 +0000\n\nJunk date\n",
                "author H <h@example.com> 1600000005 +0000\ncommitter C <c@example.com> 1600000005 +0000\n",
                "author G <g@example.com> 1600000006 +0000\ncommitter C <c@example.com> 1600000006 +0000\nencoding utf8\ngpgsig -----BEGIN PGP SIGNATURE-----\n \n abc\n -----END PGP SIGNATURE-----\n\nSigned\n",
                "committer C <c@example.com> 1600000007 +0000\n\nNo author\n",
                "author A <a@example.com> 1600000008 +0000\n\nNo committer\n",
                "author A <a@example.com> 1600000009 +0000\ncommitter Broken 1600000009 +0000\n\nCommitter without a bracket\n",
                "extra header\nauthor A <a@example.com> 1600000010 +0000\ncommitter C <c@example.com> 1600000010 +0000\n\nAn extra header first\n",
                "author A <a@example.com> 1600000011 +0000\ncommitter C <c@example.com> 1600000011 +0000\n\nNo line break at the end",
                "author A <a@example.com> 1600000012 +0000\ncommitter C <c@example.com> 1600000012 +0000\n\n\n\n",
                "\nOnly a tree and parents\n",
                "author A <a@example.com> 1600000014 +0000\nother header\ncommitter C <c@example.com> 1600000014 +0000\n\nA header between\n",
                "author A <a@example.com> 1600000013 +0000"
            ]);

        await Assert.That(Lines(InProcessHistory.Read(repo, 0, 100, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 0, 100, Cancel.None)), CollectionOrdering.Matching);
    }

    // A message in another encoding git would turn into UTF-8, so the history is left to git.
    [Test]
    public async Task LeavesOtherEncodingsToGit()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        Chain(repo, ["author A <a@example.com> 1600000000 +0000\ncommitter C <c@example.com> 1600000000 +0000\nencoding ISO-8859-1\n\nCafé\n"]);

        await Assert.That(InProcessHistory.Read(repo, 0, 100, Cancel.None)).IsNull();
        await Assert.That(Lines(InProcessHistory.ReadFaster(repo, 0, 100, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 0, 100, Cancel.None)), CollectionOrdering.Matching);
    }

    // A commit without its tree first, or with a parent that isn't an id, isn't one.
    [Test]
    [Arguments("parent 0123456789abcdef0123456789abcdef01234567\nauthor A <a@example.com> 1 +0000\n\nNo tree\n")]
    [Arguments("tree nothex\n\nBad tree\n")]
    [Arguments("tree 0123456789abcdef0123456789abcdef01234567\nparent nothex\n\nBad parent\n")]
    [Arguments("")]
    public async Task RefusesBrokenCommits(string text) =>
        await Assert.That(() => new CommitInfo(Encoding.UTF8.GetBytes(text))).Throws<GitFilesException>();

    // The most recent tag as git describe --tags finds it: going back past more than ten tags,
    // where it stops looking; across a merge, tags on each side, the one fewest commits back; of
    // two annotated on one commit, the one tagged later.
    [Test]
    public async Task DescribesAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        void Commit(string file, long time)
        {
            File.WriteAllText(Path.Combine(repo, file), file);
            CodeCommandsTests.Scratch.Git(repo, "add", file);
            var environment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = $"{time} +0000",
                ["GIT_COMMITTER_DATE"] = $"{time} +0000"
            };
            var result = GitRun.Run(repo, environment, null, "commit", "-q", "-m", file);
            if (!result.Succeeded)
            {
                throw new(result.Error);
            }
        }

        for (var index = 0; index < 13; index++)
        {
            Commit($"many{index}.txt", 1_600_000_000 + index * 60);
            CodeCommandsTests.Scratch.Git(repo, "tag", $"many{index:00}");
        }

        Commit("untagged.txt", 1_600_001_000);
        var many = await GitRun.Agree(repo);

        CodeCommandsTests.Scratch.Git(repo, "switch", "-q", "-c", "side");
        Commit("side1.txt", 1_600_002_000);
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "side-tag", "-m", "Side");
        Commit("side2.txt", 1_600_003_000);
        Commit("side3.txt", 1_600_003_500);
        CodeCommandsTests.Scratch.Git(repo, "switch", "-q", "main");
        Commit("main1.txt", 1_600_002_500);
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "main-tag", "-m", "Main");
        Commit("main2.txt", 1_600_004_000);
        CodeCommandsTests.Scratch.Git(repo, "merge", "-q", "--no-ff", "-m", "Merge", "side");
        var merged = await GitRun.Agree(repo);

        var older = new Dictionary<string, string>
        {
            ["GIT_COMMITTER_DATE"] = "1600005000 +0000"
        };
        var newer = new Dictionary<string, string>
        {
            ["GIT_COMMITTER_DATE"] = "1600006000 +0000"
        };
        GitRun.Run(repo, newer, null, "tag", "-a", "b-newer", "-m", "Newer");
        GitRun.Run(repo, older, null, "tag", "-a", "a-older", "-m", "Older");
        GitRun.Run(repo, older, null, "tag", "-a", "c-oldest", "-m", "Oldest");
        var annotated = await GitRun.Agree(repo);

        await Assert.That(many.Tag).IsNotNull();
        await Assert.That(merged.Tag).IsNotNull();
        await Assert.That(annotated.Tag).IsEqualTo("b-newer");
    }

    // A tag of a tag is the commit the last is of; a tag of a tree, or of a blob, describes no
    // commit; a tag with nothing after its headers is one all the same.
    [Test]
    public async Task PeelsTagsAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "inner", "-m", "Inner");
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "outer", "-m", "Outer", "inner");
        CodeCommandsTests.Scratch.Git(repo, "tag", "tree-tag", "HEAD^{tree}");
        CodeCommandsTests.Scratch.Git(repo, "tag", "-a", "blob-tag", "-m", "Blob", "HEAD:tracked.txt");
        var head = GitRun.Output(repo, "rev-parse", "HEAD");
        var bare = GitRun.WriteObject(repo, "tag", $"object {head}\ntype commit\ntag bare\ntagger T <t@example.com> 1600000000 +0000");
        GitRun.Output(repo, "update-ref", "refs/tags/bare", bare);
        var loose = await GitRun.Agree(repo);

        CodeCommandsTests.Scratch.Git(repo, "pack-refs", "--all");
        var packed = await GitRun.Agree(repo);

        await Assert.That(loose.Tag).IsEqualTo(packed.Tag);
        await Assert.That(Lines(InProcessHistory.Read(repo, 0, 10, Cancel.None))).IsEquivalentTo(Lines(GitHistory.Read(repo, 0, 10, Cancel.None)), CollectionOrdering.Matching);
    }

    // A tag that is corrupt, of an object not there, or of tags thirty-three deep, past where they
    // are followed here, leaves the repository to git.
    [Test]
    [Arguments("corrupt")]
    [Arguments("missing")]
    [Arguments("deep")]
    public async Task LeavesTagsItCantFollowToGit(string kind)
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        switch (kind)
        {
            case "corrupt":
                var corrupt = GitRun.WriteObject(repo, "tag", "not a tag at all\n");
                await File.WriteAllTextAsync(Path.Combine(repo, ".git", "refs", "tags", "corrupt"), corrupt + "\n");
                break;
            case "missing":
                await File.WriteAllTextAsync(Path.Combine(repo, ".git", "refs", "tags", "missing"), "0123456789abcdef0123456789abcdef01234567\n");
                break;
            case "deep":
                var target = GitRun.Output(repo, "rev-parse", "HEAD");
                for (var depth = 0; depth < 33; depth++)
                {
                    target = GitRun.WriteObject(repo, "tag", $"object {target}\ntype {(depth == 0 ? "commit" : "tag")}\ntag t{depth}\ntagger T <t@example.com> 1600000000 +0000\n\nDeep\n");
                }

                await File.WriteAllTextAsync(Path.Combine(repo, ".git", "refs", "tags", "deep"), target + "\n");
                break;
        }

        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
    }

    // Ahead and behind, and the commits not pushed, where both sides have merged branches of their
    // own since they parted, some commits dated before those they follow.
    [Test]
    public async Task CountsHowFarBranchesAreApartAsGitDoes()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        var repo = scratch.CreateRepo("Repo");
        var other = scratch.Clone("Repo", "Other");
        void Work(string where, string name, int time)
        {
            foreach (var branch in new[] { $"{name}-a", $"{name}-b" })
            {
                CodeCommandsTests.Scratch.Git(where, "switch", "-q", "-c", branch, "main");
                File.WriteAllText(Path.Combine(where, $"{branch}.txt"), branch);
                CodeCommandsTests.Scratch.Git(where, "add", ".");
                var environment = new Dictionary<string, string>
                {
                    ["GIT_COMMITTER_DATE"] = $"{1_600_000_000 + time} +0000"
                };
                GitRun.Run(where, environment, null, "commit", "-q", "-m", branch);
                time -= 100;
            }

            CodeCommandsTests.Scratch.Git(where, "switch", "-q", "main");
            CodeCommandsTests.Scratch.Git(where, "merge", "-q", "--no-ff", "-m", $"{name} a", $"{name}-a");
            CodeCommandsTests.Scratch.Git(where, "merge", "-q", "--no-ff", "-m", $"{name} b", $"{name}-b");
        }

        Work(other, "theirs", 5000);
        CodeCommandsTests.Scratch.Git(other, "push", "-q");
        Work(repo, "mine", 3000);
        CodeCommandsTests.Scratch.Git(repo, "fetch", "-q");

        var state = await GitRun.Agree(repo);

        await Assert.That((state.Ahead, state.Behind)).IsEqualTo((4, 4));
        await Assert.That(InProcessHistory.Unpushed(repo, Cancel.None)!.Order()).IsEquivalentTo(GitHistory.Unpushed(repo, Cancel.None).Order(), CollectionOrdering.Matching);
    }
}
