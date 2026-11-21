/// <summary>
/// The code folder's right-click entries, against throwaway repositories in the temp folder.
/// </summary>
public class CodeMenuTests
{
    // On origin's default branch, as the files git keeps say, read as the menu opens rather than
    // asking git: a clone on it, or a worktree on it. Not on another branch, detached, or where
    // no default branch was recorded, as in a repository made by init and given an origin later.
    [Test]
    public async Task OnTheDefaultBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        var made = code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        var onMain = CodeCommands.OnDefaultBranch(clone);
        CodeCommandsTests.Scratch.Git(clone, "switch", "-c", "feature");
        var onFeature = CodeCommands.OnDefaultBranch(clone);
        var worktree = code.At("Worktree");
        CodeCommandsTests.Scratch.Git(clone, "worktree", "add", worktree, "main");
        var worktreeOnMain = CodeCommands.OnDefaultBranch(worktree);
        CodeCommandsTests.Scratch.Git(clone, "switch", "--detach");
        var detached = CodeCommands.OnDefaultBranch(clone);

        await Assert.That((onMain, onFeature, worktreeOnMain, detached)).IsEqualTo((true, false, true, false));
        await Assert.That(CodeCommands.OnDefaultBranch(made)).IsFalse();
    }

    // Switch to default branch is left off when every repository is on it, and Push when every
    // one is known to have nothing to push. Neither is left off for a folder holding repositories,
    // which can't be told without reading them all.
    [Test]
    public async Task SwitchAndPushOnlyWhenThereIsSomethingToDo()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        var feature = code.Clone("Repo", "Feature");
        CodeCommandsTests.Scratch.Git(feature, "switch", "-c", "feature");
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var owner = new Control();
        var clean = new GitState("main", "abc1234", 0, 0, [new("new.txt", GitChange.Untracked)])
        {
            Upstream = true
        };
        string Offered(IReadOnlyList<string> folders, GitState? state)
        {
            var ids = CodeMenu.Commands(owner, settings, folders, _ => state)
                .Select(_ => _?.Id)
                .Where(_ => _ is "gitSwitchToDefault" or "gitPush")
                .Select(Name);
            return string.Join(", ", ids);
        }

        static string Name(string? id)
        {
            if (id == "gitPush")
            {
                return "push";
            }

            return "switch";
        }

        await Assert.That(Offered([clone], clean)).IsEqualTo("");
        await Assert.That(Offered([Path.Combine(clone, "sub")], clean)).IsEqualTo("");
        await Assert.That(Offered([feature], clean)).IsEqualTo("switch");
        await Assert.That(Offered([clone, feature], clean)).IsEqualTo("switch");
        await Assert.That(Offered([clone], clean with { Ahead = 1 })).IsEqualTo("push");
        await Assert.That(Offered([clone], clean with { Files = [new("tracked.txt", GitChange.Modified)] })).IsEqualTo("push");
        await Assert.That(Offered([clone], clean with { Upstream = false })).IsEqualTo("push");
        await Assert.That(Offered([clone], null)).IsEqualTo("push");
        await Assert.That(Offered([code.Root], clean)).IsEqualTo("switch, push");
    }
}
