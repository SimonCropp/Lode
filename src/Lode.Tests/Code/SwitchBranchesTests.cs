/// <summary>
/// Switch branch across several repositories, and the default branch named in the menu, against
/// throwaway repositories in the temp folder.
/// </summary>
public class SwitchBranchesTests
{
    // Only the branches every repository has, by the name each is checked out as, newest commit
    // first, less one every repository is on already. Feature is local in one and only on origin
    // in the other; spike is in one only; main is checked out in both.
    [Test]
    public async Task ChoicesAreCommonAndNewestFirst()
    {
        static GitBranches.Branch Branch(string name, int day, bool remote = false) =>
            new(name, remote, new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), false);

        var first = new CodeMenu.SwitchRead(
            "First",
            new("main", Branch("main", 1), [], [Branch("feature", 20), Branch("older", 2), Branch("spike", 25), Branch("origin/feature", 20, remote: true)]),
            null);
        var second = new CodeMenu.SwitchRead(
            "Second",
            new("main", Branch("main", 1), [], [Branch("older", 3), Branch("origin/feature", 22, remote: true)]),
            null);

        var names = CodeMenu.SwitchChoices([first, second])
            .Select(_ => _.Name)
            .ToList();

        await Assert.That(names).IsEquivalentTo(["feature", "older"], CollectionOrdering.Matching);
    }

    // Two clones of one origin: Has with feature of its own, Fetched with only origin's. Has
    // switches to its own, Fetched to a new one tracking origin's. A clone without it, cloned
    // before it was pushed, leaves it off the choices.
    [Test]
    public async Task SwitchesEachToTheBranch()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var has = code.Clone("Repo", "Has");
        var fetched = code.Clone("Repo", "Fetched");
        var without = code.Clone("Repo", "Without");
        CodeCommandsTests.Scratch.Git(has, "switch", "-c", "feature");
        CodeCommandsTests.Scratch.Git(has, "push", "-u", "origin", "feature");
        CodeCommandsTests.Scratch.Git(has, "switch", "main");
        CodeCommandsTests.Scratch.Git(fetched, "fetch");

        var withWithout = new[] { has, fetched, without }
            .Select(CodeMenu.ReadForSwitch)
            .ToList();
        await Assert.That(CodeMenu.SwitchChoices(withWithout)).IsEmpty();

        var reads = new[] { has, fetched }
            .Select(CodeMenu.ReadForSwitch)
            .ToList();
        var listed = CodeMenu.SwitchChoices(reads)
            .Select(_ => _.Name)
            .ToList();
        // Main is left off, as both have it checked out.
        await Assert.That(listed).IsEquivalentTo(["feature"], CollectionOrdering.Matching);
        await Assert.That(CodeMenu.SwitchHeading(reads)).IsEqualTo("Switch 2 repositories to:");

        var preview = CodeMenu.SwitchPreview(reads, "feature");
        await Assert.That(preview.CanGo).IsTrue();
        await Assert.That(preview.Text).IsEqualTo("Switch 2 of 2 repositories to feature: Has and Fetched.");

        var targets = CodeMenu.SwitchTargets(reads, "feature");
        foreach (var target in targets)
        {
            var result = GitBranches.Switch(target.Read.Repo, target.Read.State?.Branch, target.Branch, false, Cancel.None);
            await Assert.That(result.Succeeded).IsTrue();
        }

        await Assert.That(Branch(has)).IsEqualTo("feature");
        await Assert.That(Branch(fetched)).IsEqualTo("feature");
        await Assert.That(Branch(without)).IsEqualTo("main");

        // Now both are on it already, so it isn't offered.
        var after = new[] { has, fetched }
            .Select(CodeMenu.ReadForSwitch)
            .ToList();
        var again = CodeMenu.SwitchPreview(after, "feature");
        await Assert.That(again.CanGo).IsFalse();
        await Assert.That(CodeMenu.SwitchTargets(after, "feature")).IsEmpty();
        await Assert.That(CodeMenu.SwitchChoices(after).Select(_ => _.Name)).DoesNotContain("feature");
    }

    // For one repository, its own branches, and what switching to one would do.
    [Test]
    public async Task OneRepository()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        CodeCommandsTests.Scratch.Git(clone, "branch", "feature");
        await File.WriteAllTextAsync(Path.Combine(clone, "new.txt"), "new");

        var reads = new[] { CodeMenu.ReadForSwitch(clone) };
        await Assert.That(CodeMenu.SwitchChoices(reads).Select(_ => _.Name)).IsEquivalentTo(["feature"]);
        await Assert.That(CodeMenu.SwitchHeading(reads)).IsEqualTo("Switch Clone from main to:");
        var preview = CodeMenu.SwitchPreview(reads, "feature");

        await Assert.That(preview.CanGo).IsTrue();
        await Assert.That(preview.Text).IsEqualTo(
            """
            Switch Clone from main to feature.

            Its 1 uncommitted change: you'll be asked whether to leave them or bring them.
            """.ReplaceLineEndings("\n"));
    }

    // Switch to and Update from default branch name it, where every repository's is the same.
    [Test]
    public async Task DefaultBranchNamed()
    {
        using var code = new CodeCommandsTests.Scratch();
        code.CreateRepo("Repo");
        var clone = code.Clone("Repo", "Clone");
        CodeCommandsTests.Scratch.Git(clone, "switch", "-c", "feature");
        var settings = new Settings
        {
            CodeDirectory = code.Root
        };
        using var owner = new Control();

        var texts = CodeMenu.Commands(owner, settings, [clone])
            .Where(_ => _?.Id is "gitSwitchToDefault" or "gitUpdateFromDefault")
            .Select(_ => $"{_!.Text} | {_.NestedText}")
            .ToList();

        await Assert.That(CodeCommands.RecordedDefaultName(clone)).IsEqualTo("main");
        await Assert.That(texts).IsEquivalentTo(
            [
                "Git switch to default branch (main) | Switch to default branch (main)",
                "Git update from default branch (main) | Update from default branch (main)"
            ],
            CollectionOrdering.Matching);
    }

    // A button for each branch, newest first, under what is switched.
    [Test]
    [WindowThread]
    public async Task Dialog()
    {
        GitBranches.Branch[] branches =
        [
            new("codex/mutation-error-handling", false, null, false),
            new("master", false, null, false),
            new("saga-owned-brief-documents", false, null, false)
        ];
        using var form = new SwitchBranchForm("Switch branch", "Switch 2 repositories to:", branches, _ => "");
        form.Show();
        Application.DoEvents();
        form.Refresh();
        await Verify(form);
    }

    // Repositories with different default branches name each, the most of them first.
    [Test]
    public async Task DefaultBranchesNamedWhereTheyDiffer()
    {
        await Assert.That(CodeMenu.DefaultNames(["main"])).IsEqualTo(" (main)");
        await Assert.That(CodeMenu.DefaultNames(["master", "develop", "develop", null])).IsEqualTo(" (develop, master)");
        await Assert.That(CodeMenu.DefaultNames(["a", "b", "c", "d", "d"])).IsEqualTo(" (d, a, b, …)");
        await Assert.That(CodeMenu.DefaultNames([null])).IsEqualTo("");
    }

    static string Branch(string repo) =>
        CodeCommands.Git(repo, "branch", "--show-current").Output.Trim();
}
