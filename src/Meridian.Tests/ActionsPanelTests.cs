/// <summary>
/// The log of running and finished commands, and the panel under the tree that shows it. The
/// log is shared by every window, so these take turns with the other window tests and empty it
/// when done.
/// </summary>
[TUnit.Core.Executors.STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ActionsPanelTests
{
    [After(Test)]
    public void Empty() =>
        ActionLog.Reset();

    [Test]
    public async Task AnEntryRunsThenRecordsTheOutcome()
    {
        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        var run = ActionLog.Run("Git pull", @"C:\Code\Meridian", () => finish.Task);
        var running = Describe();

        finish.SetResult(new(false, "1 of 2 failed", "Bad: FAILED"));
        await run;

        await Verify(
            new
            {
                running,
                finished = Describe()
            });
    }

    // Work that throws still ends as a failed entry, rather than taking Meridian down.
    [Test]
    public async Task WorkThatThrowsFails()
    {
        await ActionLog.Run("Update NuGets", @"C:\Code\Meridian", () => throw new InvalidOperationException("No network"));

        await Verify(Describe());
    }

    [Test]
    public async Task ClearLeavesRunningEntries()
    {
        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        await ActionLog.Run("Git pull", @"C:\Code\Done", () => Task.FromResult(new ActionLog.Outcome(true, "Done", "")));
        _ = ActionLog.Run("Update NuGets", @"C:\Code\Busy", () => finish.Task);

        ActionLog.ClearFinished();

        await Verify(Describe());
        finish.SetResult(new(true, "Done", ""));
    }

    [Test]
    public async Task PanelShowsOnlyWithEntries()
    {
        using var panel = CreatePanel();
        var empty = panel.Visible;

        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        _ = ActionLog.Run("Update NuGets", @"C:\Code\Busy", () => finish.Task);
        var whileRunning = new
        {
            panel.Visible,
            clearEnabled = Clear(panel).Enabled
        };

        finish.SetResult(new(true, "5 packages updated", ""));
        await Pump.Until(() => ActionLog.Entries[0].State != ActionLog.State.Running);
        var finished = new
        {
            panel.Visible,
            clearEnabled = Clear(panel).Enabled,
            lines = Lines(panel)
        };

        ActionLog.ClearFinished();

        await Verify(
            new
            {
                empty,
                whileRunning,
                finished,
                afterClear = panel.Visible
            });
    }

    // Visible means "would be shown", so the panel sits on a form that is shown, off screen.
    static ActionsPanel CreatePanel()
    {
        var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-20000, -20000)
        };
        var panel = new ActionsPanel();
        form.Controls.Add(panel);
        form.Show();
        panel.Disposed += (_, _) => form.Dispose();
        return panel;
    }

    static LinkLabel Clear(Control panel) =>
        (LinkLabel) panel.Controls.Find("clearActions", true)[0];

    static List<string> Lines(Control panel) =>
        ((ListBox) panel.Controls.Find("actions", true)[0]).Items
        .Cast<object>()
        .Select(_ => _.ToString()!)
        .ToList();

    static List<object> Describe() =>
        ActionLog.Entries
            .Select(object (_) => new
            {
                line = _.ToString(),
                state = _.State.ToString(),
                _.Details
            })
            .ToList();
}
