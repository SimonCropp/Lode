/// <summary>
/// The log of running and finished commands, and the panel under the tree that shows it. The
/// log is shared by every window, so these take turns with the other window tests and empty it
/// when done.
/// </summary>
[STAThreadExecutor]
// WinForms keeps one unlocked table of every window it creates, so windows made on two
// threads at once can corrupt it. Every test that makes windows takes turns.
[NotInParallel("windows")]
public class ActionsPanelTests
{
    [After(Test)]
    public void Empty() =>
        ActionLog.Reset();

    // An entry for several folders names both of two, or the first of more and how many others;
    // its details name every one.
    [Test]
    public async Task AnEntryForSeveralFolders()
    {
        var one = new ActionLog.Entry("Git pull", [@"C:\Code\Lode"]);
        var drive = new ActionLog.Entry("Git pull", [@"D:\"]);
        var two = new ActionLog.Entry("Git pull", [@"C:\Code\Lode", @"C:\Code\Verify"]);
        var three = new ActionLog.Entry("Git pull", [@"C:\Code\Lode", @"C:\Code\Verify", @"C:\Code\Polyfill"]);

        await Assert.That(one.FolderName).IsEqualTo("Lode");
        await Assert.That(one.Where).IsEqualTo(@"C:\Code\Lode");
        await Assert.That(drive.FolderName).IsEqualTo(@"D:\");
        await Assert.That(two.FolderName).IsEqualTo("Lode and Verify");
        await Assert.That(three.FolderName).IsEqualTo("Lode and 2 more");
        await Assert.That(three.Where).IsEqualTo("Lode, Verify, Polyfill");
    }

    // A minute after it finished, an entry that went well, or was cancelled, leaves the list, so
    // it needn't be cleared by hand. One that failed stays, to be seen, as does one still
    // running, and one that finished since.
    [Test]
    public async Task FinishedEntriesAgeOut()
    {
        var clock = new Clock(new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        ActionLog.Time = clock;
        try
        {
            static Task<ActionLog.Outcome> Ended(bool succeeded) =>
                Task.FromResult(new ActionLog.Outcome(succeeded, "", ""));

            await ActionLog.Run("Git pull", @"C:\Code\Done", () => Ended(true));
            await ActionLog.Run("Git pull", @"C:\Code\Failed", () => Ended(false));
            var cancelling = ActionLog.Run(
                "Git pull",
                [@"C:\Code\Cancelled"],
                async (_, cancel) =>
                {
                    await Task.Delay(Timeout.Infinite, cancel);
                    return new(true, "", "");
                });
            ActionLog.Entries[^1].Cancel();
            await cancelling;
            _ = ActionLog.Run("Git pull", @"C:\Code\Running", () => new TaskCompletionSource<ActionLog.Outcome>().Task);
            clock.Now += TimeSpan.FromSeconds(59);
            await ActionLog.Run("Git pull", @"C:\Code\Since", () => Ended(true));

            static List<string> Names() =>
                ActionLog.Entries.Select(_ => _.FolderName).ToList();

            ActionLog.AgeOut();
            var before = Names();
            clock.Now += TimeSpan.FromSeconds(1);
            ActionLog.AgeOut();

            await Assert.That(before).IsEquivalentTo(["Done", "Failed", "Cancelled", "Running", "Since"], CollectionOrdering.Matching);
            await Assert.That(Names()).IsEquivalentTo(["Failed", "Running", "Since"], CollectionOrdering.Matching);
        }
        finally
        {
            ActionLog.Time = TimeProvider.System;
        }
    }

    // One that went well, but reported something to come back for, the commit a deleted branch
    // was at, says it stays, and does, until cleared.
    [Test]
    public async Task AnEntryThatSaysItStaysDoesNotAgeOut()
    {
        var clock = new Clock(new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        ActionLog.Time = clock;
        try
        {
            await ActionLog.Run(
                "Git delete branch feature",
                @"C:\Code\Lode",
                () => Task.FromResult(
                    new ActionLog.Outcome(true, "Done", "Deleted branch feature (was 1a2b3c4).")
                    {
                        Stays = true
                    }));
            clock.Now += TimeSpan.FromHours(1);
            ActionLog.AgeOut();

            await Assert.That(ActionLog.Entries.Select(_ => _.Details)).IsEquivalentTo(["Deleted branch feature (was 1a2b3c4)."]);
        }
        finally
        {
            ActionLog.Time = TimeProvider.System;
        }
    }

    // Work that can be stopped can be cancelled while it runs. It ends as failed, "Cancelled",
    // with what it did till then in its details. Work that can't be stopped can't be cancelled.
    [Test]
    public async Task ARunningEntryCanBeCancelled()
    {
        var run = ActionLog.Run(
            "Git pull",
            [@"C:\Code\Lode"],
            async (_, cancel) =>
            {
                await Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => { }, TaskScheduler.Default);
                return new(true, "Done", "Lode: done");
            });
        var entry = ActionLog.Entries.Single();
        var couldCancel = entry.CanCancel;

        entry.Cancel();
        var whileStopping = (entry.Summary, entry.CanCancel);
        await run;
        _ = ActionLog.Run("Git pull", @"C:\Code\Other", () => new TaskCompletionSource<ActionLog.Outcome>().Task);

        await Assert.That(couldCancel).IsTrue();
        await Assert.That(whileStopping).IsEqualTo(("Cancelling…", false));
        await Assert.That(entry.State).IsEqualTo(ActionLog.State.Failed);
        await Assert.That(entry.Summary).IsEqualTo("Cancelled");
        await Assert.That(entry.Details).IsEqualTo("Lode: done");
        await Assert.That(ActionLog.Entries[^1].CanCancel).IsFalse();
    }

    // What Exit says of the entries still running, which exiting would stop part way. Not of
    // those finished.
    [Test]
    public async Task RunningMessage()
    {
        var none = ActionLog.RunningMessage();
        await ActionLog.Run("Git pull", @"C:\Code\Done", () => Task.FromResult(new ActionLog.Outcome(true, "Done", "")));
        var onlyFinished = ActionLog.RunningMessage();
        _ = ActionLog.Run("Git purge", @"C:\Code\Lode", () => new TaskCompletionSource<ActionLog.Outcome>().Task);
        var one = ActionLog.RunningMessage();
        foreach (var name in new[] { "Verify", "Polyfill", "Argon", "Delta", "Replicant" })
        {
            _ = ActionLog.Run("Update NuGets", $@"C:\Code\{name}", () => new TaskCompletionSource<ActionLog.Outcome>().Task);
        }

        await Verify(
            new
            {
                none,
                onlyFinished,
                one,
                many = ActionLog.RunningMessage()
            });
    }

    [Test]
    public async Task AnEntryRunsThenRecordsTheOutcome()
    {
        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        var run = ActionLog.Run("Git pull", @"C:\Code\Lode", () => finish.Task);
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

    // What the work reports is the summary while it runs; the outcome replaces it, and a
    // report arriving after that is dropped.
    [Test]
    public async Task ProgressShowsUntilTheEntryEnds()
    {
        var finish = new TaskCompletionSource<ActionLog.Outcome>();
        IProgress<string>? reporter = null;
        var run = ActionLog.Run(
            "Git pull",
            @"C:\Code",
            progress =>
            {
                reporter = progress;
                return finish.Task;
            });

        await Task.Run(() => reporter!.Report("2 of 12: Lode"));
        await Pump.Until(() => ActionLog.Entries[0].Summary != "Running…");
        var running = Describe();

        finish.SetResult(new(true, "12 repositories done", ""));
        await run;
        reporter!.Report("12 of 12: Verify");
        Application.DoEvents();

        await Verify(
            new
            {
                running,
                finished = Describe()
            });
    }

    // Work that throws still ends as a failed entry, rather than taking Lode down.
    [Test]
    public async Task WorkThatThrowsFails()
    {
        await ActionLog.Run("Update NuGets", @"C:\Code\Lode", () => throw new InvalidOperationException("No network"));

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
            clearEnabled = Clear(panel).Enabled,
            panel.Spinning
        };

        finish.SetResult(new(true, "5 packages updated", ""));
        await Pump.Until(() => ActionLog.Entries[0].State != ActionLog.State.Running);
        var finished = new
        {
            panel.Visible,
            clearEnabled = Clear(panel).Enabled,
            panel.Spinning,
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
