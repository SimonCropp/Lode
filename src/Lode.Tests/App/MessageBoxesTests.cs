/// <summary>
/// A box Lode puts up in a test fails it with what the box says, rather than the box waiting for
/// a click nobody gives, and the run with it. Run through WindowThread's executor, so that the
/// failing is what is tested.
/// </summary>
public class MessageBoxesTests
{
    // The box that held a run, once another run deleted the folder a test was showing: going
    // somewhere that isn't there.
    [Test]
    public async Task ABoxFailsItsTestWithWhatItSays()
    {
        var missing = Path.Combine(Path.GetTempPath(), "LodeTests", $"Missing.{Guid.NewGuid():N}");

        var thrown = await Failed(() =>
        {
            using var view = new FolderView(new(), new([], [], ShowComputer: false, _ => ""));
            view.Navigate(missing);
            return default;
        });

        await Assert.That(thrown).IsEquivalentTo([$"Lode put up a message box, which nobody in a test run is there to answer. Lode: Lode can't find '{missing}'. Check the spelling and try again."]);
    }

    // A task dialog likewise, saying its heading and text.
    [Test]
    public async Task ATaskDialogFailsItsTestWithWhatItSays()
    {
        var thrown = await Failed(() =>
        {
            MessageBoxes.ShowDialog(
                new TaskDialogPage
                {
                    Caption = "Lode",
                    Heading = "Replace?",
                    Text = "It can't be undone.",
                    Buttons = {TaskDialogButton.OK, TaskDialogButton.Cancel}
                });
            return default;
        });

        await Assert.That(thrown).IsEquivalentTo(["Lode put up a task dialog, which nobody in a test run is there to answer. Lode: Replace? It can't be undone."]);
    }

    // Put up by an async void method, as a click's handler often is, whose throw the test
    // thread's synchronization context drops: the box is kept for the test all the same.
    [Test]
    public async Task ABoxWhoseThrowIsDroppedStillFailsItsTest()
    {
        var thrown = await Failed(async () =>
        {
            PutUpLater();
            await Task.Delay(200);
        });

        await Assert.That(thrown).IsEquivalentTo(["Lode put up a message box, which nobody in a test run is there to answer. Lode: Dropped"]);

        static async void PutUpLater()
        {
            await Task.Yield();
            MessageBoxes.Show("Dropped", "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Every box goes through MessageBoxes, for tests to stop.
    [Test]
    public async Task NoBoxPutUpAnyOtherWay()
    {
        var source = Path.Combine(AttributeReader.GetSolutionDirectory(), "Lode");
        var direct = new List<string>();
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(@"obj\") ||
                relative.StartsWith(@"bin\") ||
                relative == @"App\MessageBoxes.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("MessageBox.Show(") ||
                    lines[i].Contains("TaskDialog.ShowDialog("))
                {
                    direct.Add($"{relative}:{i + 1}");
                }
            }
        }

        await Assert.That(direct).IsEmpty();
    }

    // The messages of what the run failed with, once each. Should a box come up after all, it is
    // closed, so the run doesn't wait on it, and the test fails saying so.
    static async Task<List<string>> Failed(Func<ValueTask> action)
    {
        uint thread = 0;
        var run = new WindowThreadExecutor()
            .ExecuteTest(
                TestContext.Current!,
                () =>
                {
                    thread = GetCurrentThreadId();
                    return action();
                })
            .AsTask();

        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))) != run)
        {
            const int close = 0x10;
            EnumThreadWindows(
                thread,
                (window, _) =>
                {
                    PostMessage(window, close, IntPtr.Zero, IntPtr.Zero);
                    return true;
                },
                IntPtr.Zero);
            Assert.Fail("A box came up");
        }

        var thrown = await Assert.That(() => run).Throws<Exception>();
        IEnumerable<Exception> all = [thrown!];
        if (thrown is AggregateException aggregate)
        {
            all = aggregate.Flatten().InnerExceptions;
        }

        return all
            .Select(_ => _.Message)
            .Distinct()
            .ToList();
    }

    delegate bool WindowVisitor(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(uint thread, WindowVisitor visit, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
