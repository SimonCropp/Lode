/// <summary>
/// The diff of a new file of 100,000 lines, all one run of changes, painted with the pointer
/// over the strip beside the numbers, which draws the run the pointer is over lighter: as it is
/// painted again for every row the pointer moves down the strip. Painted to a bitmap, as the
/// canvas is never shown.
/// </summary>
[MemoryDiagnoser]
public class DiffCanvasBenchmarks
{
    const int lineCount = 100_000;
    const int mouseMove = 0x0200;

    DiffCanvas canvas = null!;
    Bitmap bitmap = null!;

    [GlobalSetup]
    public void Setup()
    {
        var lines = Enumerable.Range(1, lineCount)
            .Select(_ => $"        Step{_}();")
            .ToArray();
        var patch = new StringBuilder($"--- /dev/null\n+++ b/New.cs\n@@ -0,0 +1,{lineCount} @@\n");
        foreach (var line in lines)
        {
            patch.Append('+').Append(line).Append('\n');
        }

        var diff = (TextDiff) DiffParser.Parse(patch.ToString(), evenIfLarge: true);
        canvas = new()
        {
            Size = new(800, 600)
        };
        canvas.CreateControl();
        canvas.Show(DiffLayout.Of(diff, lines, split: false), sideBySide: false, LinePicks.All(diff), null, keepPlace: false);

        // Half way down, the pointer over the strip.
        canvas.TopRow = lineCount / 2;
        var point = canvas.StripPointOf(canvas.TopRow + 10);
        NativeMethods.SendMessage(canvas.Handle, mouseMove, 0, (point.Y << 16) | (point.X & 0xFFFF));
        bitmap = new(canvas.Width, canvas.Height);

        // What is timed is the run drawn lighter, so it must be.
        PaintOverTheStrip();
        if (bitmap.GetPixel(point.X, point.Y).ToArgb() != DiffStyle.PickedHover.ToArgb())
        {
            throw new("The pointer isn't over the strip, so the run isn't drawn lighter.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        bitmap.Dispose();
        canvas.Dispose();
    }

    [Benchmark]
    public void PaintOverTheStrip() =>
        canvas.DrawToBitmap(bitmap, new(0, 0, canvas.Width, canvas.Height));
}
