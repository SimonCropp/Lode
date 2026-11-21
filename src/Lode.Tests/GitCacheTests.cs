/// <summary>
/// When a repository whose files are changing is read again, and what of git's own writing is
/// no reason to. The times are given, in milliseconds, so nothing waits.
/// </summary>
public class GitCacheTests
{
    const long start = 100_000;

    static GitState Clean() =>
        new("main", null, 0, 0, []);

    static GitState OneChange() =>
        new("main", null, 0, 0, [new("tracked.txt", GitChange.Modified)]);

    // Read once, as a view going to it reads it, and found clean.
    static GitCache.Known ReadOnce()
    {
        var known = new GitCache.Known();
        known.Read(Clean(), null, known.Changes, start);
        return known;
    }

    // A change to its files every tenth of a second for as long as given, as a build's output
    // has it, read whenever it is due, each read finding what found gives for the time since
    // the start. When it was read, in seconds since the start.
    static List<long> Churn(GitCache.Known known, long length, Func<long, GitState> found)
    {
        var reads = new List<long>();
        for (var elapsed = 100L; elapsed <= length; elapsed += 100)
        {
            var now = start + elapsed;
            known.Changed(now, inGit: false);
            if (known.Due > now)
            {
                continue;
            }

            known.Told();
            known.Read(found(elapsed), null, known.Changes, now);
            reads.Add(elapsed / 1000);
        }

        return reads;
    }

    // Changes going on, a read finding nothing new is followed by the next twice as long after,
    // up to a minute, rather than every three seconds, each a read of every file.
    [Test]
    public async Task ChangesThatGoOnWithNothingNewAreReadLessAndLessOften()
    {
        var known = ReadOnce();

        var reads = Churn(known, 300_000, _ => Clean());

        await Assert.That(reads)
            .IsEquivalentTo([3L, 6, 12, 24, 48, 96, 156, 216, 276], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Something new found, at 24 seconds here, the next read is as soon after as the first was.
    // That finding the same again, they are further apart once more.
    [Test]
    public async Task SomethingNewFoundIsFollowedAsCloselyAsAtFirst()
    {
        var known = ReadOnce();

        var reads = Churn(
            known,
            40_000,
            elapsed =>
            {
                if (elapsed < 20_000)
                {
                    return Clean();
                }

                return OneChange();
            });

        await Assert.That(reads)
            .IsEquivalentTo([3L, 6, 12, 24, 27, 30, 36], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Held, it counts as fresh, so nothing reads it: not the views told, nor one gone to, nor
    // the folder read again as the build changes it.
    [Test]
    public async Task HeldItIsFresh()
    {
        var known = ReadOnce();
        Churn(known, 30_000, _ => Clean());
        var now = start + 30_100;

        known.Changed(now, inGit: false);

        await Assert.That(known.Fresh(now)).IsTrue();
    }

    // A commit, a stage or a pull is what a state is made of: however long it was held for, it
    // is read once that has settled.
    [Test]
    public async Task AChangeInGitEndsTheHold()
    {
        var known = ReadOnce();
        Churn(known, 30_000, _ => Clean());
        var now = start + 30_100;

        known.Changed(now, inGit: true);

        await Assert.That(known.Fresh(now)).IsFalse();
        await Assert.That(known.Due - now).IsEqualTo(500);
    }

    // Held until 48 seconds, its changes stopping at 30, it is read at 33: what a build left
    // is not waited another 15 seconds for.
    [Test]
    public async Task ItsChangesStoppedItIsReadHoweverLongItWasHeldFor()
    {
        var known = ReadOnce();

        Churn(known, 30_000, _ => Clean());

        await Assert.That(known.Due - start).IsEqualTo(33_000);
        await Assert.That(known.Fresh(start + 32_999)).IsTrue();
        await Assert.That(known.Fresh(start + 33_000)).IsFalse();
    }

    // A file saved once the build is over is read as soon as one ever is.
    [Test]
    public async Task ChangesBegunAgainAreReadAsSoonAsAtFirst()
    {
        var known = ReadOnce();
        Churn(known, 30_000, _ => Clean());
        known.Told();
        known.Read(Clean(), null, known.Changes, start + 33_000);
        var now = start + 40_000;

        known.Changed(now, inGit: false);

        await Assert.That(known.Fresh(now)).IsFalse();
        await Assert.That(known.Due - now).IsEqualTo(500);
    }

    // Two views showing the one repository each read it. The second finds what the first did,
    // which says nothing of whether its changes are ones a state shows.
    [Test]
    public async Task AnotherViewReadingTheSameDoesNotHoldIt()
    {
        var known = ReadOnce();
        var now = start + 10_000;
        known.Changed(now, inGit: false);
        known.Told();
        var changes = known.Changes;
        known.Read(OneChange(), null, changes, now + 500);

        known.Read(OneChange(), null, changes, now + 510);
        known.Changed(now + 1_000, inGit: false);

        await Assert.That(known.Fresh(now + 1_000)).IsFalse();
    }

    [Test]
    public async Task WhatGitWritesForItselfIsNoise()
    {
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\objects")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\objects\3f\9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\objects\pack\tmp_pack_a1b2c3")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\logs\HEAD")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\logs\refs\heads\main")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\fsmonitor--daemon\cookies")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\fsmonitor--daemon\cookies\20376-1")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\index.lock")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\refs\heads\main.lock")).IsTrue();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.GIT\Objects\3f")).IsTrue();
    }

    // What a state is made of, in .git and out of it. Nor is what only looks like git's own:
    // a folder or a lock of the project's, and .git itself, made by init or deleted.
    [Test]
    public async Task WhatAStateIsMadeOfIsNotNoise()
    {
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\index")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\HEAD")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\refs\heads\main")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\refs\remotes\origin\main")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\packed-refs")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\config")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\info\exclude")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git\objectsmoved")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.git")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\.gitignore")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\src\Thing.cs")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\objects\Thing.cs")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\logs\today.log")).IsFalse();
        await Assert.That(GitCache.IsNoise(@"C:\Code\Repo\yarn.lock")).IsFalse();
    }
}
