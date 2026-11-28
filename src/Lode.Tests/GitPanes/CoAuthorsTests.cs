using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

/// <summary>
/// Co-authors, as Changes adds them to a commit: what is typed read as Name &lt;email&gt;, the
/// trailers written as git reads them, the history's authors suggested, and a commit's trailers
/// taken back out to commit again.
/// </summary>
public class CoAuthorsTests
{
    static readonly CoAuthors.Author grace = new("Grace Hopper", "grace@example.com");
    static readonly CoAuthors.Author alan = new("Alan Turing", "alan@example.com");
    static readonly CoAuthors.Author ada = new("Ada Lovelace", "ada@example.com");

    // Each Name <email>, whatever is between them, the same email once; anything else, a name
    // with no email yet say, none at all, so Commit waits for it.
    [Test]
    public async Task Parse()
    {
        await Assert.That(CoAuthors.Parse("")).IsEquivalentTo(Array.Empty<CoAuthors.Author>());
        await Assert.That(CoAuthors.Parse(" , ")).IsEquivalentTo(Array.Empty<CoAuthors.Author>());
        await Assert.That(CoAuthors.Parse("Grace Hopper <grace@example.com>, Alan Turing <alan@example.com>; ")).IsEquivalentTo([grace, alan]);
        await Assert.That(CoAuthors.Parse("Grace Hopper<grace@example.com>Alan Turing <alan@example.com>")).IsEquivalentTo([grace, alan]);
        await Assert.That(CoAuthors.Parse("Grace Hopper <grace@example.com>, G. Hopper <GRACE@example.com>")).IsEquivalentTo([grace]);
        await Assert.That(CoAuthors.Parse("Hopper, Grace <grace@example.com>")).IsEquivalentTo([new CoAuthors.Author("Hopper, Grace", "grace@example.com")]);

        await Assert.That(CoAuthors.Parse("Grace Hopper")).IsNull();
        await Assert.That(CoAuthors.Parse("Grace Hopper <grace@example.com>, Alan")).IsNull();
        await Assert.That(CoAuthors.Parse("Grace Hopper <grace@example.com")).IsNull();
        await Assert.That(CoAuthors.Parse("<grace@example.com>")).IsNull();
        await Assert.That(CoAuthors.Parse("Grace Hopper <>")).IsNull();
        await Assert.That(CoAuthors.Parse("Grace Hopper <grace @example.com>")).IsNull();
    }

    // The trailers after a blank line, a paragraph of their own after the summary or the
    // description, or among the trailers the description ends in, so git reads them all; one
    // there already not added again. As git interpret-trailers reads them, and as the commit made
    // with the message has them.
    [Test]
    public async Task TrailersInGitsForm()
    {
        using var scratch = new Scratch();
        var summaryOnly = CoAuthors.WithTrailers(GitChanges.Message("Fix", ""), [grace, alan]);
        var described = CoAuthors.WithTrailers(GitChanges.Message("Fix", "Why.\r\nAnd more.\r\n"), [grace]);
        var signed = CoAuthors.WithTrailers(GitChanges.Message("Fix", "Why.\r\n\r\nSigned-off-by: Test <test@example.com>"), [grace]);
        var already = CoAuthors.WithTrailers("Fix\n\nCo-authored-by:Grace Hopper <grace@example.com>", [grace, alan]);

        await Assert.That(CoAuthors.WithTrailers("Fix", [])).IsEqualTo("Fix");
        await Assert.That(summaryOnly).IsEqualTo("Fix\n\nCo-authored-by: Grace Hopper <grace@example.com>\nCo-authored-by: Alan Turing <alan@example.com>");
        await Assert.That(described).IsEqualTo("Fix\n\nWhy.\nAnd more.\n\nCo-authored-by: Grace Hopper <grace@example.com>");
        await Assert.That(signed).IsEqualTo("Fix\n\nWhy.\n\nSigned-off-by: Test <test@example.com>\nCo-authored-by: Grace Hopper <grace@example.com>");
        await Assert.That(already).IsEqualTo("Fix\n\nCo-authored-by:Grace Hopper <grace@example.com>\nCo-authored-by: Alan Turing <alan@example.com>");

        async Task<string> Parsed(string message)
        {
            var file = scratch.At($"{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(file, message);
            return Git(scratch.Root, "interpret-trailers", "--parse", file);
        }

        await Assert.That(await Parsed(summaryOnly)).IsEqualTo("Co-authored-by: Grace Hopper <grace@example.com>\nCo-authored-by: Alan Turing <alan@example.com>");
        await Assert.That(await Parsed(described)).IsEqualTo("Co-authored-by: Grace Hopper <grace@example.com>");
        await Assert.That(await Parsed(signed)).IsEqualTo("Signed-off-by: Test <test@example.com>\nCo-authored-by: Grace Hopper <grace@example.com>");

        var repo = scratch.CreateRepo("Repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "edited");
        var result = GitChanges.Commit(repo, GitChanges.Read(repo, Cancel.None)!.Files, merging: false, described, Cancel.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Git(repo, "log", "-1", "--format=%(trailers:key=Co-authored-by,valueonly)")).IsEqualTo("Grace Hopper <grace@example.com>");
        await Assert.That(Git(repo, "log", "-1", "--format=%B")).IsEqualTo(described);
    }

    // Whether a paragraph is trailers, as git takes the last to be: every line Token: value, or
    // one carried on, indented.
    [Test]
    public async Task IsTrailerBlock()
    {
        await Assert.That(CoAuthors.IsTrailerBlock("Signed-off-by: Test <test@example.com>")).IsTrue();
        await Assert.That(CoAuthors.IsTrailerBlock("Fixes: #12\nCo-authored-by: Grace Hopper <grace@example.com>")).IsTrue();
        await Assert.That(CoAuthors.IsTrailerBlock("Long-key: a value\n  carried on")).IsTrue();
        await Assert.That(CoAuthors.IsTrailerBlock("Why it was done.")).IsFalse();
        await Assert.That(CoAuthors.IsTrailerBlock("Why: it was done.\nAnd then more")).IsFalse();
        await Assert.That(CoAuthors.IsTrailerBlock("Not a key here: value")).IsFalse();
        await Assert.That(CoAuthors.IsTrailerBlock("")).IsFalse();
    }

    // A commit's message put back to commit again: the co-authors its trailers name come out of
    // the description, into the co-authors, the rest of the description kept.
    [Test]
    public async Task Take()
    {
        await Assert.That(CoAuthors.Take("Why.")).IsEqualTo(("Why.", ""));
        await Assert.That(CoAuthors.Take("Co-authored-by: Grace Hopper <grace@example.com>")).IsEqualTo(("", "Grace Hopper <grace@example.com>"));
        await Assert.That(CoAuthors.Take("Why.\n\nCo-authored-by: Grace Hopper <grace@example.com>\nco-authored-by: Alan Turing <alan@example.com>")).IsEqualTo(("Why.", "Grace Hopper <grace@example.com>, Alan Turing <alan@example.com>"));
        await Assert.That(CoAuthors.Take("Why.\n\nCo-authored-by: Grace Hopper <grace@example.com>\nSigned-off-by: Test <test@example.com>")).IsEqualTo(("Why.\n\nSigned-off-by: Test <test@example.com>", "Grace Hopper <grace@example.com>"));

        // Only the trailers: a line of the description that says co-authored isn't one.
        await Assert.That(CoAuthors.Take("Co-authored-by: Grace Hopper <grace@example.com>\n\nWhy.")).IsEqualTo(("Co-authored-by: Grace Hopper <grace@example.com>\n\nWhy.", ""));
    }

    // What is being typed matches the history's authors by name or email, but for those typed
    // already; picked, it is put in place of what was typed, with a comma after for the next.
    [Test]
    public async Task SuggestAndPick()
    {
        IReadOnlyList<CoAuthors.Author> authors = [grace, alan, ada];

        await Assert.That(CoAuthors.Suggest(authors, "", 0).Matches).IsEmpty();
        var (start, end, matches) = CoAuthors.Suggest(authors, "gr", 2);
        await Assert.That((start, end)).IsEqualTo((0, 2));
        await Assert.That(matches).IsEquivalentTo([grace]);
        await Assert.That(CoAuthors.Suggest(authors, "turing", 6).Matches).IsEquivalentTo([alan]);
        await Assert.That(CoAuthors.Suggest(authors, "ada@", 4).Matches).IsEquivalentTo([ada]);

        var typed = "Grace Hopper <grace@example.com>, a";
        (start, end, matches) = CoAuthors.Suggest(authors, typed, typed.Length);
        await Assert.That((start, end)).IsEqualTo((typed.Length - 1, typed.Length));
        await Assert.That(matches).IsEquivalentTo([alan, ada], CollectionOrdering.Matching);

        var (text, caret) = CoAuthors.Pick(typed, start, end, ada);
        await Assert.That(text).IsEqualTo("Grace Hopper <grace@example.com>, Ada Lovelace <ada@example.com>, ");
        await Assert.That(caret).IsEqualTo(text.Length);

        // In the middle, what follows is kept, after the comma.
        typed = "Grace Hopper <grace@example.com>, tur, Ada Lovelace <ada@example.com>";
        (start, end, matches) = CoAuthors.Suggest(authors, typed, typed.IndexOf("tur", StringComparison.Ordinal) + 3);
        await Assert.That(matches).IsEquivalentTo([alan]);
        (text, caret) = CoAuthors.Pick(typed, start, end, alan);
        await Assert.That(text).IsEqualTo("Grace Hopper <grace@example.com>, Alan Turing <alan@example.com>, Ada Lovelace <ada@example.com>");
        await Assert.That(text[..caret]).IsEqualTo("Grace Hopper <grace@example.com>, Alan Turing <alan@example.com>, ");

        // A co-author typed whole has nothing to suggest.
        typed = "Grace Hopper <grace@example.com>";
        await Assert.That(CoAuthors.Suggest(authors, typed, typed.Length).Matches).IsEmpty();
        await Assert.That(CoAuthors.Suggest(authors, typed, 3).Matches).IsEmpty();
    }

    // Who has committed lately, as the history names them, newest first, a .mailmap mapping their
    // names and emails, each email once, and not who is committing now. None before the first
    // commit.
    [Test]
    public async Task SuggestionsComeFromTheHistory()
    {
        using var scratch = new Scratch();
        var repo = scratch.CreateRepo("Repo");
        Git(repo, "commit", "--allow-empty", "-m", "Ada's", "--author=Ada Lovelace <ada@example.com>");
        Git(repo, "commit", "--allow-empty", "-m", "Grace's", "--author=Grace Hopper <grace@example.com>");
        Git(repo, "commit", "--allow-empty", "-m", "Grace's, by her old email", "--author=G. Hopper <ghopper@old.example.com>");
        Git(repo, "commit", "--allow-empty", "-m", "Alan's", "--author=Alan Turing <alan@example.com>");
        Git(repo, "commit", "--allow-empty", "-m", "Their own");
        await File.WriteAllTextAsync(Path.Combine(repo, ".mailmap"), "Grace Hopper <grace@example.com> <ghopper@old.example.com>\n");

        var read = CoAuthors.Read(repo, Cancel.None);

        await Assert.That(read.Select(_ => _.Text)).IsEquivalentTo(
            [
                "Alan Turing <alan@example.com>",
                "Grace Hopper <grace@example.com>",
                "Ada Lovelace <ada@example.com>"
            ],
            CollectionOrdering.Matching);

        var empty = scratch.At("Empty");
        Directory.CreateDirectory(empty);
        Git(empty, "init", "--initial-branch=main");

        await Assert.That(CoAuthors.Read(empty, Cancel.None)).IsEmpty();
    }
}
