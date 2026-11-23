using static CodeCommandsTests;
using static CodeCommandsTests.Scratch;

// One at a time: each test points GitDiff.Folder, which is shared, at a scratch folder of its
// own and puts it back after. Run together, one wrote its versions into the other's folder, or
// put the folder back under it, and VersionIsAsCheckedOut failed now and then.
[NotInParallel("GitDiff.Folder")]
public class GitDiffTests
{
    // A version written as git would check it out: with autocrlf, the line endings the working
    // file has, so only what changed shows as changed. Named as the file is, read only, and the
    // same file each time it is asked for.
    [Test]
    public async Task VersionIsAsCheckedOut()
    {
        using var scratch = new Scratch();
        var folder = GitDiff.Folder;
        GitDiff.Folder = scratch.At("Diffs");
        try
        {
            var repo = scratch.CreateRepo("Repo");
            Git(repo, "config", "core.autocrlf", "true");
            await File.WriteAllTextAsync(Path.Combine(repo, "lines.txt"), "one\ntwo\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-m", "Lines");

            var version = GitDiff.Version(repo, "HEAD", "lines.txt")!;

            await Assert.That(Path.GetFileName(version)).IsEqualTo("lines.txt");
            await Assert.That(await File.ReadAllTextAsync(version)).IsEqualTo("one\r\ntwo\r\n");
            await Assert.That(File.GetAttributes(version).HasFlag(FileAttributes.ReadOnly)).IsTrue();
            await Assert.That(GitDiff.Version(repo, "HEAD", "lines.txt")).IsEqualTo(version);
            await Assert.That(GitDiff.Version(repo, "HEAD", "missing.txt")).IsNull();
        }
        finally
        {
            GitDiff.Folder = folder;
        }
    }

    // HEAD is taken as the commit it is at, so a commit since writes the new version, not the one
    // written before.
    [Test]
    public async Task HeadFollowsCommits()
    {
        using var scratch = new Scratch();
        var folder = GitDiff.Folder;
        GitDiff.Folder = scratch.At("Diffs");
        try
        {
            var repo = scratch.CreateRepo("Repo");
            var before = GitDiff.Version(repo, "HEAD", "tracked.txt")!;
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
            Git(repo, "commit", "-am", "Change");

            var after = GitDiff.Version(repo, "HEAD", "tracked.txt")!;

            await Assert.That(after).IsNotEqualTo(before);
            await Assert.That(await File.ReadAllTextAsync(after)).IsEqualTo("changed");
        }
        finally
        {
            GitDiff.Folder = folder;
        }
    }
}
