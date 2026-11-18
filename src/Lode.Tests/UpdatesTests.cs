public class UpdatesTests
{
    static readonly DateTimeOffset now = TestUpdates.Now;

    // A day after the last check, and not before, unless the clock was put back.
    [Test]
    public Task IsDue() =>
        Verify(
            new
            {
                neverChecked = Updates.IsDue(null, now),
                anHourAgo = Updates.IsDue(now.AddHours(-1), now),
                justUnderADayAgo = Updates.IsDue(now.AddDays(-1).AddMinutes(1), now),
                aDayAgo = Updates.IsDue(now.AddDays(-1), now),
                aWeekAgo = Updates.IsDue(now.AddDays(-7), now),
                inTheFuture = Updates.IsDue(now.AddHours(1), now)
            });

    // The update is by the tool path above the store, and says how it went beside the settings,
    // outside the store it replaces. ToolUpdateTests has the shim, and a build's output having
    // none.
    [Test]
    public Task FindsTheToolItIsInstalledAs()
    {
        var update = Updates.Find(TestUpdates.Store)!;
        return Verify(
            new
            {
                update.ToolPath,
                outcomeBesideTheSettings = update.Outcome == Updates.OutcomePath
            });
    }

    [Test]
    public async Task ACheckFindsANewerVersion()
    {
        var settings = new Settings();
        var updates = TestUpdates.Installed(settings, "0.4.0-beta.1");
        var changes = 0;
        updates.Changed += () => changes++;

        await updates.Check();

        await Verify(
            new
            {
                available = Available(updates),
                updates.Error,
                updates.Checking,
                settings.LastUpdateCheck,
                settings.AvailableUpdate,
                // As it starts and as it ends.
                changes
            });
    }

    [Test]
    public async Task ACheckFindsNothingNewer()
    {
        var same = new Settings();
        await TestUpdates.Installed(same, "0.3.0").Check();
        var older = new Settings();
        await TestUpdates.Installed(older, "0.2.0").Check();

        await Verify(
            new
            {
                same = Describe(same),
                older = Describe(older)
            });
    }

    // Offline, say: what the last check found still shows, and the check stays due, so the next
    // hour tries again.
    [Test]
    public async Task AFailedCheckKeepsWhatWasKnown()
    {
        var lastChecked = now.AddDays(-2);
        var settings = new Settings
        {
            LastUpdateCheck = lastChecked,
            AvailableUpdate = "0.4.0"
        };
        var updates = TestUpdates.Installed(
            settings,
            _ => throw new HttpRequestException("No such host is known. (api.nuget.org:443)"));

        await updates.Check();

        await Verify(
            new
            {
                available = Available(updates),
                updates.Error,
                updates.Checking,
                lastCheckedUnchanged = settings.LastUpdateCheck == lastChecked,
                stillDue = Updates.IsDue(settings.LastUpdateCheck, now)
            });
    }

    // A pending update shows from the start, rather than a day later. Not once it is the version
    // running, as it is after updating, nor in a build, which can't update.
    [Test]
    public Task WhatTheLastCheckFoundShowsAtStart() =>
        Verify(
            new
            {
                newer = Available(
                    TestUpdates.Installed(
                        new()
                        {
                            AvailableUpdate = "0.4.0"
                        })),
                nowRunning = Available(
                    TestUpdates.Installed(
                        new()
                        {
                            AvailableUpdate = "0.3.0"
                        })),
                unreadable = Available(
                    TestUpdates.Installed(new()
                    {
                        AvailableUpdate = "soon"
                    })),
                build = Available(
                    TestUpdates.Build(
                        new()
                        {
                            AvailableUpdate = "0.4.0"
                        }))
            });

    [Test]
    public async Task ChecksOnceADay()
    {
        var clock = new Clock(now);
        var settings = new Settings
        {
            LastUpdateCheck = now.AddHours(-2)
        };
        var asked = 0;
        var updates = TestUpdates.Installed(
            settings,
            _ =>
            {
                asked++;
                return Task.FromResult<NuGetVersion?>(null);
            },
            clock);

        await updates.CheckIfDue();
        var twoHoursOn = asked;
        clock.Now = now.AddHours(22);
        await updates.CheckIfDue();
        var aDayOn = asked;
        clock.Now = now.AddHours(23);
        await updates.CheckIfDue();

        // Whether NuGet was asked: two hours after the last check, a day after it, and an hour
        // after that.
        await Verify(
            new
            {
                twoHoursOn = twoHoursOn > 0,
                aDayOn = aDayOn > twoHoursOn,
                anHourAfterThat = asked > aDayOn
            });
    }

    // The Lode the update starts says how it went, once. A success says the version rather than
    // what the tool said, and not as a warning, which would make an update that worked look like
    // one that had not. A failure says why.
    [Test]
    public async Task ReportsHowTheUpdateWent()
    {
        var outcome = Path.Combine(Path.GetTempPath(), $"UpdatesTests.{Guid.NewGuid():N}.log");
        try
        {
            var updates = Reporting(outcome);

            var none = updates.Report() == null;
            await File.WriteAllTextAsync(outcome, "ok\nTool 'lode' was successfully updated from version '0.2.0' to version '0.3.0'.\n");
            var succeeded = updates.Report();
            var noneAgain = updates.Report() == null;
            await File.WriteAllTextAsync(outcome, "failed\nUnhandled exception: Access to the path 'lode.dll' is denied.\n");
            var failed = updates.Report();

            await Verify(
                new
                {
                    none,
                    succeeded,
                    noneAgain,
                    failed
                });
        }
        finally
        {
            File.Delete(outcome);
        }
    }

    // A file that cannot be read says nothing, rather than throwing out of Lode's start, and is
    // left for the next start. A file open with no sharing is what Windows refuses to read.
    [Test]
    public async Task AnOutcomeThatCannotBeReadSaysNothing()
    {
        var outcome = Path.Combine(Path.GetTempPath(), $"UpdatesTests.{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(outcome, "failed\nUnhandled exception: Access to the path 'lode.dll' is denied.\n");
            var updates = Reporting(outcome);
            UpdateNotice? notice;
            using (new FileStream(outcome, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                notice = updates.Report();
            }

            await Verify(
                new
                {
                    saysNothing = notice == null,
                    left = File.Exists(outcome)
                });
        }
        finally
        {
            File.Delete(outcome);
        }
    }

    // Installed, with how the update went written to outcome.
    static Updates Reporting(string outcome) =>
        new(
            new(),
            new(@"C:\Users\Someone\.dotnet\tools\lode.exe", outcome),
            TestUpdates.Current,
            _ => Task.FromResult<NuGetVersion?>(null),
            new Clock(now));

    static object Describe(Settings settings) =>
        new
        {
            checkedAt = settings.LastUpdateCheck,
            available = settings.AvailableUpdate ?? "none"
        };

    static string Available(Updates updates) =>
        updates.Available?.ToNormalizedString() ?? "none";
}
