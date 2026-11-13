/// <summary>
/// What Show system files covers, against a made-up machine.
/// </summary>
public class SystemItemsTests
{
    static readonly SystemItems items = new(
        @"C:\Windows",
        [@"C:\Program Files", @"C:\Program Files (x86)"],
        @"C:\Users",
        [@"C:\Users\Me", @"C:\Users\Public"]);

    const FileAttributes folder = FileAttributes.Directory;

    [Test]
    public Task IsSystem() =>
        Verify(
            new
            {
                windows = items.IsSystem(@"C:\Windows", folder),
                programFiles = items.IsSystem(@"C:\Program Files", folder),
                programFilesX86 = items.IsSystem(@"C:\Program Files (x86)", folder),
                insideProgramFiles = items.IsSystem(@"C:\Program Files\Git", folder),
                anotherUser = items.IsSystem(@"C:\Users\Someone", folder),
                myFolder = items.IsSystem(@"C:\Users\Me", folder),
                publicFolder = items.IsSystem(@"C:\Users\Public", folder),
                insideAnotherUser = items.IsSystem(@"C:\Users\Someone\Documents", folder),
                fileInUsers = items.IsSystem(@"C:\Users\desktop.ini", FileAttributes.Archive),
                hiddenAndSystem = items.IsSystem(@"D:\$RECYCLE.BIN", folder | FileAttributes.Hidden | FileAttributes.System),
                systemButNotHidden = items.IsSystem(@"D:\Customised", folder | FileAttributes.System),
                hiddenOnly = items.IsSystem(@"D:\.git", folder | FileAttributes.Hidden),
                ordinaryFolder = items.IsSystem(@"C:\Code", folder)
            });
}
