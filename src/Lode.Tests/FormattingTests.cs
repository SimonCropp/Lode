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
}
