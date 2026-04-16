using Meridian.Services;

namespace Tests.Services;

[TestFixture]
class FileSystemServiceTests
{
    string tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "MeridianTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void GetDirectoryContents_FoldersFirst()
    {
        File.WriteAllText(Path.Combine(tempDir, "file.txt"), "content");
        Directory.CreateDirectory(Path.Combine(tempDir, "subfolder"));

        var items = FileSystemService.GetDirectoryContents(tempDir);

        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items[0].IsDirectory, Is.True);
        Assert.That(items[0].Name, Is.EqualTo("subfolder"));
        Assert.That(items[1].IsDirectory, Is.False);
        Assert.That(items[1].Name, Is.EqualTo("file.txt"));
    }

    [Test]
    public void GetDirectoryContents_SortedAlphabetically()
    {
        Directory.CreateDirectory(Path.Combine(tempDir, "Zebra"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Apple"));
        File.WriteAllText(Path.Combine(tempDir, "banana.txt"), "");
        File.WriteAllText(Path.Combine(tempDir, "apple.txt"), "");

        var items = FileSystemService.GetDirectoryContents(tempDir);

        Assert.That(items[0].Name, Is.EqualTo("Apple"));
        Assert.That(items[1].Name, Is.EqualTo("Zebra"));
        Assert.That(items[2].Name, Is.EqualTo("apple.txt"));
        Assert.That(items[3].Name, Is.EqualTo("banana.txt"));
    }

    [Test]
    public void GetDirectoryContents_FileSize()
    {
        File.WriteAllText(Path.Combine(tempDir, "test.txt"), "hello world");

        var items = FileSystemService.GetDirectoryContents(tempDir);

        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0].Size, Is.GreaterThan(0));
    }

    [Test]
    public void CreateDirectory_CreatesFolder()
    {
        var path = Path.Combine(tempDir, "newdir");

        FileSystemService.CreateDirectory(path);

        Assert.That(Directory.Exists(path), Is.True);
    }

    [Test]
    public void Rename_RenamesFile()
    {
        var original = Path.Combine(tempDir, "original.txt");
        File.WriteAllText(original, "content");

        FileSystemService.Rename(original, "renamed.txt");

        Assert.That(File.Exists(original), Is.False);
        Assert.That(File.Exists(Path.Combine(tempDir, "renamed.txt")), Is.True);
    }

    [Test]
    public void Delete_DeletesFile()
    {
        var path = Path.Combine(tempDir, "todelete.txt");
        File.WriteAllText(path, "content");

        FileSystemService.Delete(path);

        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void Delete_DeletesDirectory()
    {
        var path = Path.Combine(tempDir, "todeleteDir");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inner.txt"), "content");

        FileSystemService.Delete(path);

        Assert.That(Directory.Exists(path), Is.False);
    }

    [Test]
    public void Copy_CopiesFile()
    {
        var source = Path.Combine(tempDir, "source.txt");
        var dest = Path.Combine(tempDir, "dest.txt");
        File.WriteAllText(source, "content");

        FileSystemService.Copy(source, dest);

        Assert.That(File.Exists(source), Is.True);
        Assert.That(File.Exists(dest), Is.True);
        Assert.That(File.ReadAllText(dest), Is.EqualTo("content"));
    }

    [Test]
    public void Move_MovesFile()
    {
        var source = Path.Combine(tempDir, "moveme.txt");
        var dest = Path.Combine(tempDir, "moved.txt");
        File.WriteAllText(source, "content");

        FileSystemService.Move(source, dest);

        Assert.That(File.Exists(source), Is.False);
        Assert.That(File.Exists(dest), Is.True);
    }

    [Test]
    public void Exists_ReturnsTrueForFile()
    {
        var path = Path.Combine(tempDir, "exists.txt");
        File.WriteAllText(path, "content");

        Assert.That(FileSystemService.Exists(path), Is.True);
    }

    [Test]
    public void Exists_ReturnsTrueForDirectory() =>
        Assert.That(FileSystemService.Exists(tempDir), Is.True);

    [Test]
    public void Exists_ReturnsFalseForMissing() =>
        Assert.That(FileSystemService.Exists(Path.Combine(tempDir, "nope")), Is.False);
}
