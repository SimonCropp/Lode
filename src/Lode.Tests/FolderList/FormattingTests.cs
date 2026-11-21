public class FormattingTests
{
    static readonly long[] sizes =
    [
        0,
        1,
        1_023,
        1_024,
        1_025,
        10_240,
        150_000,
        1_048_576,
        1_500_000,
        123_456_789,
        1_073_741_824,
        500_107_862_016,
        2_000_398_934_016
    ];

    [Test]
    public Task Size() =>
        Verify(sizes.Select(_ => $"{_:N0} = {Formatting.Size(_)}"));

    [Test]
    public Task Kilobytes() =>
        Verify(sizes.Select(_ => $"{_:N0} = {Formatting.Kilobytes(_)}"));

    // Midnight and noon, a morning and an afternoon, each hour in two digits, am and pm in
    // lower case right after it.
    [Test]
    public async Task Modified()
    {
        await Assert.That(Formatting.Modified(new(2026, 11, 1, 13, 11, 0))).IsEqualTo("01/11/2026 01:11pm");
        await Assert.That(Formatting.Modified(new(2026, 3, 27, 7, 26, 0))).IsEqualTo("27/03/2026 07:26am");
        await Assert.That(Formatting.Modified(new(2026, 12, 31, 0, 5, 0))).IsEqualTo("31/12/2026 12:05am");
        await Assert.That(Formatting.Modified(new(2026, 6, 15, 12, 0, 0))).IsEqualTo("15/06/2026 12:00pm");
    }
}
