/// <summary>
/// The PowerShell that excludes the code folder, which only ever runs from the Options button.
/// </summary>
public class DefenderExclusionTests
{
    // A quote in a path is doubled, so it can't end the string early.
    [Test]
    public Task Script() =>
        Verify(DefenderExclusion.Script(@"D:\Bob's Code", @"C:\Temp\result.txt"));
}
