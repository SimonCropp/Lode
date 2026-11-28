using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// The background fetch of the repositories open in tabs: on its interval, as its clock says,
/// once at a time for each repository, not while the Actions panel runs anything on one, nor
/// when it is off, and never asking anything. From scratch repositories, fetched for real, or
/// with the fetch stood in for, to see when it runs.
/// </summary>
public class BackgroundFetchTests
{
    static BackgroundFetch Fetcher(Settings settings, IReadOnlyList<string> open, TimeProvider time, Func<string, Cancel, CodeCommands.RepoResult> fetch, Func<IReadOnlyList<string>>? busy = null, List<IReadOnlyList<string>>? fetched = null) =>
        new(settings, () => open, time, busy ?? (() => []), _ => fetched?.Add(_), fetch);

    static Settings InCode(CodeCommandsTests.Scratch code) =>
        new()
        {
            CodeDirectory = code.Root
        };

    // A fetch stood in for, counting the repositories it is asked to fetch.
    sealed class Counting
    {
        public ConcurrentQueue<string> Fetched { get; } = new();

        public CodeCommands.RepoResult Fetch(string repo, Cancel cancel)
        {
            Fetched.Enqueue(repo);
            return new(repo, true, "");
        }
    }

    // Each repository open is fetched once it is due: at once, never fetched before, then once
    // the interval has gone by since, and not before. What origin has comes in, as the status
    // bar's behind counts it, and those fetched are given, to be read again.
    [Test]
    public async Task FetchesTheRepositoriesOpenOnItsInterval()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var elsewhere = code.Clone("Repo", "Elsewhere");
        var clock = new Clock(DateTimeOffset.UtcNow);
        var settings = InCode(code);
        settings.FetchMinutes = 30;
        var fetched = new List<IReadOnlyList<string>>();
        using var fetcher = Fetcher(settings, [repo], clock, CodeCommands.FetchQuietly, fetched: fetched);

        var first = Commit(elsewhere, "first");
        await fetcher.Tick();
        var atOnce = OriginMain(repo);
        var second = Commit(elsewhere, "second");
        clock.Now += TimeSpan.FromMinutes(29);
        await fetcher.Tick();
        var notYet = OriginMain(repo);
        clock.Now += TimeSpan.FromMinutes(2);
        await fetcher.Tick();
        var due = OriginMain(repo);

        await Assert.That(atOnce).IsEqualTo(first);
        await Assert.That(notYet).IsEqualTo(first);
        await Assert.That(due).IsEqualTo(second);
        await Assert.That(fetched.Select(_ => string.Join(", ", _))).IsEquivalentTo([repo, repo]);
    }

    // A fetch made anywhere else, in a terminal say, counts, as FETCH_HEAD says when it was, so
    // the repository waits the interval from then.
    [Test]
    public async Task AFetchFromElsewhereCounts()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        CodeCommandsTests.Scratch.Git(repo, "fetch");
        var clock = new Clock(DateTimeOffset.UtcNow);
        var counting = new Counting();
        using var fetcher = Fetcher(InCode(code), [repo], clock, counting.Fetch);

        await fetcher.Tick();
        var fetchedElsewhere = counting.Fetched.Count;
        clock.Now += TimeSpan.FromMinutes(61);
        await fetcher.Tick();

        await Assert.That(fetchedElsewhere).IsEqualTo(0);
        await Assert.That(counting.Fetched).IsEquivalentTo([repo]);
    }

    // One that fails, offline say, is tried again only once the interval has gone by, not at
    // every tick.
    [Test]
    public async Task AFailedFetchWaitsTheInterval()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var clock = new Clock(DateTimeOffset.UtcNow);
        var tries = 0;
        using var fetcher = Fetcher(
            InCode(code),
            [repo],
            clock,
            (_, _) =>
            {
                Interlocked.Increment(ref tries);
                return new(repo, false, "fatal: unable to access");
            });

        await fetcher.Tick();
        clock.Now += TimeSpan.FromMinutes(1);
        await fetcher.Tick();
        var beforeDue = tries;
        clock.Now += TimeSpan.FromMinutes(60);
        await fetcher.Tick();

        await Assert.That(beforeDue).IsEqualTo(1);
        await Assert.That(tries).IsEqualTo(2);
    }

    // Off, as Options sets it, nothing is fetched, however long it has been.
    [Test]
    public async Task NothingWhenOff()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var clock = new Clock(DateTimeOffset.UtcNow);
        var counting = new Counting();
        var settings = InCode(code);
        settings.FetchInBackground = false;
        using var fetcher = Fetcher(settings, [repo], clock, counting.Fetch);

        await fetcher.Tick();
        clock.Now += TimeSpan.FromDays(3);
        await fetcher.Tick();

        await Assert.That(counting.Fetched).IsEmpty();
    }

    // Each folder open stands for the repository it is in, so two tabs in one, at its top and
    // in a folder inside it, are one fetch. Lode's git is all in the code folder: a repository
    // outside it isn't fetched, nor is any without a code folder.
    [Test]
    public async Task OnlyTheRepositoriesTheFoldersAreInInTheCodeFolder()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var inside = Path.Combine(repo, "src");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(code.At("Code"));
        var clock = new Clock(DateTimeOffset.UtcNow);
        var counting = new Counting();
        var settings = new Settings
        {
            CodeDirectory = code.At("Code")
        };
        using var fetcher = Fetcher(settings, [inside, repo, code.At("Code")], clock, counting.Fetch);

        await fetcher.Tick();
        settings.CodeDirectory = null;
        await fetcher.Tick();
        var outside = counting.Fetched.ToList();
        settings.CodeDirectory = code.Root;
        await fetcher.Tick();

        await Assert.That(outside).IsEmpty();
        await Assert.That(counting.Fetched).IsEquivalentTo([repo]);
    }

    // On by default, hourly, as GitHub Desktop fetches; the minutes Options takes, between five
    // and a day, whatever the settings file says.
    [Test]
    public async Task OnHourlyByDefault()
    {
        var settings = new Settings();

        await Assert.That(settings.FetchInBackground).IsTrue();
        await Assert.That(BackgroundFetch.Interval(settings)).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(BackgroundFetch.Interval(new() {FetchMinutes = 0})).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(BackgroundFetch.Interval(new() {FetchMinutes = 100_000})).IsEqualTo(TimeSpan.FromDays(1));
    }

    // A repository is fetched once at a time: a tick while its fetch is still going leaves it,
    // and its worktree too, which shares its branches, as two tabs on it are one. Once that fetch
    // has ended, and it is due again, it is fetched again.
    [Test]
    public async Task NeverTwiceAtOnce()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var worktree = code.At("Repo-feature");
        CodeCommandsTests.Scratch.Git(repo, "worktree", "add", "-b", "feature", worktree);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var started = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource();
        using var fetcher = Fetcher(
            InCode(code),
            [repo, worktree, repo],
            clock,
            (fetching, _) =>
            {
                started.Enqueue(fetching);
                release.Task.Wait();
                return new(fetching, true, "");
            });

        var first = fetcher.Tick();
        await WaitFor(() => !started.IsEmpty);
        clock.Now += TimeSpan.FromHours(2);
        await fetcher.Tick().WaitAsync(TimeSpan.FromSeconds(30));
        var whileFetching = started.ToList();
        release.SetResult();
        await first;
        clock.Now += TimeSpan.FromHours(2);
        await fetcher.Tick();

        await Assert.That(whileFetching).IsEquivalentTo([repo]);
        await Assert.That(started).IsEquivalentTo([repo, repo]);
    }

    // Not while the Actions panel runs anything on it, one of Lode's git actions say: on a
    // worktree of it, which shares its branches, or a folder inside it, or one holding it. Once
    // those have ended, it is. The Actions panel is every window's, so this takes turns with the
    // window tests.
    [Test]
    [WindowThread]
    [NotInParallel("windows")]
    public async Task NotWhileAGitActionRunsOnIt()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var worktree = code.At("Repo-feature");
        CodeCommandsTests.Scratch.Git(repo, "worktree", "add", "-b", "feature", worktree);
        var inside = code.CreateRepo("Inside");
        code.CreateRepo("Held");
        var held = code.Clone("Held", Path.Combine("Group", "Held"));
        var clock = new Clock(DateTimeOffset.UtcNow);
        var counting = new Counting();
        using var fetcher = Fetcher(InCode(code), [repo, inside, held], clock, counting.Fetch, BackgroundFetch.RunningFolders);
        var release = new TaskCompletionSource<ActionLog.Outcome>();
        List<ActionLog.Entry> ended;
        try
        {
            var running = new[]
            {
                ActionLog.Run("Git pull", worktree, () => release.Task),
                ActionLog.Run("Git push", Path.Combine(inside, "src"), () => release.Task),
                ActionLog.Run("Git fetch", code.At("Group"), () => release.Task)
            };
            await fetcher.Tick();
            var whileRunning = counting.Fetched.ToList();
            release.SetResult(new(true, "Done", ""));
            ended = [.. await Task.WhenAll(running)];
            await fetcher.Tick();

            await Assert.That(whileRunning).IsEmpty();
        }
        finally
        {
            ActionLog.Reset();
        }

        await Assert.That(ended.Select(_ => _.State)).IsEquivalentTo([ActionLog.State.Succeeded, ActionLog.State.Succeeded, ActionLog.State.Succeeded]);
        await Assert.That(counting.Fetched).IsEquivalentTo([repo, inside, held], CollectionOrdering.Any);
    }

    // Nobody asked for it, so it never asks anything: git fails where it would have to ask for a
    // password, rather than showing a prompt, and doesn't run the program core.askPass names, or
    // GIT_ASKPASS, the user's or an editor's terminal's, which would; and the credential helper,
    // Git Credential Manager say, is told not to ask either, by its own setting and switch. It
    // fails at once, as nothing waits on an answer. Given a program to ask, git would: the
    // origin really does want a password.
    [Test]
    public async Task NeverAsks()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        using var site = new SignInWanted();
        var asked = code.At("asked.log");
        var helped = code.At("helped.log");
        var askPass = code.At("askpass.sh");
        await File.WriteAllTextAsync(askPass, $"#!/bin/sh\necho \"$1\" >> '{Slashed(asked)}'\necho someone\n");
        CodeCommandsTests.Scratch.Git(repo, "remote", "set-url", "origin", site.Url);
        CodeCommandsTests.Scratch.Git(repo, "config", "http.proxy", "");
        CodeCommandsTests.Scratch.Git(repo, "config", "core.askPass", Slashed(askPass));
        // In place of every helper the user's config has, one that says how it was asked, and
        // has nothing to give.
        CodeCommandsTests.Scratch.Git(repo, "config", "--add", "credential.helper", "");
        CodeCommandsTests.Scratch.Git(repo, "config", "--add", "credential.helper", $"!f() {{ echo \"$1 $GCM_INTERACTIVE $(git config --get credential.interactive)\" >> '{Slashed(helped)}'; }}; f");
        var started = Stopwatch.StartNew();

        var quiet = await Task.Run(() => CodeCommands.FetchQuietly(repo, Cancel.None)).WaitAsync(TimeSpan.FromSeconds(60));
        var took = started.Elapsed;
        var askedQuietly = File.Exists(asked);
        var helperAsked = await File.ReadAllLinesAsync(helped);
        var allowed = CodeCommands.Git(repo, Cancel.None, network: true, null, ["fetch"], new Dictionary<string, string> {["GIT_ASKPASS"] = Slashed(askPass)});

        await Assert.That(quiet.Succeeded).IsFalse();
        await Assert.That(quiet.Output).Contains("terminal prompts disabled");
        await Assert.That(took).IsLessThan(TimeSpan.FromSeconds(30));
        await Assert.That(askedQuietly).IsFalse();
        await Assert.That(helperAsked[0]).IsEqualTo("get never false");
        await Assert.That(allowed.Succeeded).IsFalse();
        await Assert.That(File.Exists(asked)).IsTrue();
    }

    static string Slashed(string path) =>
        path.Replace('\\', '/');

    /// <summary>
    /// Stands in for a site that wants signing in: every request is answered 401, asking for a
    /// password, on the loopback address, so nothing leaves the machine.
    /// </summary>
    sealed class SignInWanted : IDisposable
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        CancelSource stopping = new();

        public SignInWanted()
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}/octo/repo.git";
            _ = Answer();
        }

        public string Url { get; }

        async Task Answer()
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stopping.Token);
                }
                catch (Exception exception)
                    when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => Refuse(client));
            }
        }

        // Once the request's head has come, to its blank line.
        static async Task Refuse(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            return;
                        }

                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await stream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"Lode tests\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                }
                catch (IOException)
                {
                    // Git gave up on it first.
                }
            }
        }

        public void Dispose()
        {
            stopping.Cancel();
            listener.Stop();
            stopping.Dispose();
        }
    }

    static string Commit(string repo, string file)
    {
        File.WriteAllText(Path.Combine(repo, $"{file}.txt"), file);
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", file);
        CodeCommandsTests.Scratch.Git(repo, "push", "origin", "main");
        return CodeCommandsTests.Scratch.Git(repo, "rev-parse", "HEAD");
    }

    static string OriginMain(string repo) =>
        CodeCommandsTests.Scratch.Git(repo, "rev-parse", "origin/main");

    static async Task WaitFor(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }
}
