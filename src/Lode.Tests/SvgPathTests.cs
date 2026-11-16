using System.Drawing.Drawing2D;

public class SvgPathTests
{
    // Relative points from where each segment starts, numbers run together where a sign or a
    // second decimal point starts the next, and a curve whose points are all from its start.
    [Test]
    public async Task Parse()
    {
        using var path = SvgPath.Parse("m10 10 5-.5.5.5h-1v2c1 1 2 2 3 3L20 20Z");

        await Verify(
            path.PathPoints.Zip(
                path.PathTypes,
                (point, type) => $"{point.X} {point.Y} {(PathPointType)(type & (byte)PathPointType.PathTypeMask)}"));
    }

    [Test]
    public async Task NumbersAfterACloseAreRefused() =>
        await Assert.That(() => SvgPath.Parse("M0 0L1 1Z 2 2")).Throws<FormatException>();
}
