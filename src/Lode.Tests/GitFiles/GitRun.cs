using System.Diagnostics;
using System.Text;

/// <summary>
/// Git run as the oracle for what is read in process: with an environment of the test's own, and
/// input, and what it said and how it ended given back whatever it was, as a refusal is what is
/// being compared, as often as an answer.
/// </summary>
static class GitRun
{
    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Succeeded => ExitCode == 0;
    }

    // Leaves out what another test may have put in this process's environment for its own git.
    static readonly string[] leftOut = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"];

    public static Result Run(string directory, IReadOnlyDictionary<string, string>? environment, byte[]? input, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in leftOut)
        {
            startInfo.Environment.Remove(name);
        }

        if (environment != null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input != null)
        {
            process.StandardInput.BaseStream.Write(input);
        }

        process.StandardInput.Close();
        process.WaitForExit();
        return new(process.ExitCode, output.Result, error.Result);
    }

    public static Result Run(string directory, params string[] arguments) =>
        Run(directory, null, null, arguments);

    /// <summary>
    /// Git's answer, which must be one.
    /// </summary>
    public static string Output(string directory, params string[] arguments)
    {
        var result = Run(directory, arguments);
        if (!result.Succeeded)
        {
            throw new($"git {string.Join(' ', arguments)} failed in {directory}: {result.Error}");
        }

        return result.Output.TrimEnd('\n');
    }

    /// <summary>
    /// An object written as it is, as git hash-object --literally writes one, however malformed:
    /// what git itself does with it is the oracle.
    /// </summary>
    public static string WriteObject(string repo, string type, byte[] content)
    {
        var result = Run(repo, null, content, "hash-object", "--literally", "-t", type, "-w", "--stdin");
        if (!result.Succeeded)
        {
            throw new($"git hash-object failed in {repo}: {result.Error}");
        }

        return result.Output.Trim();
    }

    public static string WriteObject(string repo, string type, string content) =>
        WriteObject(repo, type, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// What git status and the in-process reader read, which must agree.
    /// </summary>
    public static async Task<GitState> Agree(string repo)
    {
        var git = GitState.FromGit(repo, Cancel.None);
        var inProcess = InProcessState.FromFiles(repo, Cancel.None);
        await Assert.That(git).IsNotNull();
        await Assert.That(inProcess).IsEqualTo(git);
        return git!;
    }

    /// <summary>
    /// A repository that isn't read in process, as git would read it differently, so is left to
    /// git, which reads it as it always does.
    /// </summary>
    public static async Task LeftToGit(string repo)
    {
        await Assert.That(InProcessState.FromFiles(repo, Cancel.None)).IsNull();
        await Assert.That(InProcessState.FromFaster(repo, Cancel.None)).IsEqualTo(GitState.FromGit(repo, Cancel.None));
    }
}
