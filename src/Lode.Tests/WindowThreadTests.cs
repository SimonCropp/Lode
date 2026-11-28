using System.Reflection;

/// <summary>
/// Window tests run on a thread that fails them on what a window's message handler throws,
/// rather than WinForms putting up its error dialog and the run waiting on it.
/// </summary>
public class WindowThreadTests
{
    // Thrown in a control's WndProc, caught by WinForms there, and the test fails with it once
    // it ends, with no dialog put up. Run here through the executor, so its failing is what is
    // tested: should the dialog come up, it is closed, so the run doesn't wait on it.
    [Test]
    public async Task AWindowsErrorFailsItsTest()
    {
        var ran = false;
        var run = new WindowThreadExecutor()
            .ExecuteTest(
                TestContext.Current!,
                () =>
                {
                    using var control = new Throwing();
                    NativeMethods.SendMessage(control.Handle, Throwing.Throws, IntPtr.Zero, IntPtr.Zero);
                    ran = true;
                    return default;
                })
            .AsTask();

        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))) != run)
        {
            foreach (var dialog in Application.OpenForms.OfType<ThreadExceptionDialog>().ToList())
            {
                dialog.BeginInvoke(dialog.Close);
            }

            Assert.Fail("WinForms put up its error dialog");
        }

        var thrown = await Assert.That(() => run).Throws<AggregateException>();
        await Assert.That(ran).IsTrue();
        await Assert.That(thrown!.InnerExceptions.Single().Message).IsEqualTo("Thrown from WndProc");
    }

    // Every window test gets the thread: STAThreadExecutor gives one without the handler.
    [Test]
    public async Task NoTestRunsOnAPlainStaThread()
    {
        var plain = typeof(WindowThreadTests).Assembly
            .GetTypes()
            .SelectMany(_ => _.GetMethods().Cast<MemberInfo>().Prepend(_))
            .Where(_ => _.IsDefined(typeof(STAThreadExecutorAttribute)))
            .Select(_ => _.Name)
            .ToList();

        await Assert.That(plain).IsEmpty();
    }

    sealed class Throwing : Control
    {
        public const int Throws = 0x8000 + 1;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Throws)
            {
                throw new InvalidOperationException("Thrown from WndProc");
            }

            base.WndProc(ref message);
        }
    }
}
