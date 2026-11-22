/// <summary>
/// What git doesn't ignore in a repository, as Find in Files searches it: every file git tracks,
/// and every one it doesn't that no .gitignore, info/exclude or core.excludesFile leaves out,
/// as <c>git ls-files --cached --others --exclude-standard</c> lists them. A tracked file in an
/// ignored folder is in it, as git still tracks it. The folders that hold any of them are what
/// the walk goes into, so an ignored folder, node_modules or bin say, is never walked at all. A
/// repository nested in it, or a submodule, is listed as a folder of its own, which the walk
/// goes into and asks of its own files.
/// </summary>
sealed class GitScope
{
    // Looked up by a part of the full path, so asking makes no string for every file.
    HashSet<string>.AlternateLookup<CharSpan> files;
    HashSet<string>.AlternateLookup<CharSpan> folders;

    /// <summary>
    /// The repository's top folder.
    /// </summary>
    public string Top { get; }

    GitScope(string top, HashSet<string> files, HashSet<string> folders)
    {
        Top = top;
        this.files = files.GetAlternateLookup<CharSpan>();
        this.folders = folders.GetAlternateLookup<CharSpan>();
    }

    /// <summary>
    /// How many files it holds.
    /// </summary>
    public int Count => files.Set.Count;

    /// <summary>
    /// Whether the walk goes into a folder in the repository: one holding a file git doesn't
    /// ignore, or a repository of its own.
    /// </summary>
    public bool HasFolder(string fullPath)
    {
        var relative = Relative(fullPath);
        if (relative.IsEmpty)
        {
            return false;
        }

        return folders.Contains(relative) ||
               files.Contains(relative);
    }

    /// <summary>
    /// Whether git doesn't ignore a file in the repository.
    /// </summary>
    public bool HasFile(string fullPath)
    {
        var relative = Relative(fullPath);
        if (relative.IsEmpty)
        {
            return false;
        }

        return files.Contains(relative);
    }

    // Where it is from the top, or nothing for anywhere else.
    CharSpan Relative(string fullPath)
    {
        if (fullPath.Length <= Top.Length + 1 ||
            !fullPath.StartsWith(Top, StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        var start = Top.Length;
        if (Top[^1] != '\\')
        {
            if (fullPath[start] != '\\')
            {
                return default;
            }

            start++;
        }

        return fullPath.AsSpan(start);
    }

    /// <summary>
    /// Made from what <c>git ls-files -z</c> wrote: paths from the top, with forward slashes,
    /// each ended by a nul. A repository nested in it, untracked, ends in a slash.
    /// </summary>
    internal static GitScope Parse(string top, string listed)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var range in listed.AsSpan().Split('\0'))
        {
            var path = listed[range].Replace('/', '\\');
            if (path.Length == 0)
            {
                continue;
            }

            if (path[^1] == '\\')
            {
                path = path[..^1];
                folders.Add(path);
            }
            else
            {
                files.Add(path);
            }

            for (var slash = path.LastIndexOf('\\'); slash > 0; slash = path.LastIndexOf('\\', slash - 1))
            {
                if (!folders.Add(path[..slash]))
                {
                    break;
                }
            }
        }

        return new(top.TrimEnd('\\'), files, folders);
    }

    /// <summary>
    /// Lists it, by asking git. Null when git couldn't: it isn't installed, or won't take the
    /// repository, one owned by another user say.
    /// </summary>
    public static GitScope? List(string top, Cancel cancel)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = top,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in (string[]) ["--no-optional-locks", "ls-files", "-z", "--cached", "--others", "--exclude-standard"])
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["LC_ALL"] = "C";
            using var process = Process.Start(startInfo)!;
            using var stopping = cancel.Register(() => Stop(process));

            // Only what it lists: what it says on the side, a folder it couldn't open say,
            // would otherwise run into the last path.
            var error = process.StandardError.ReadToEndAsync(Cancel.None);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = error.Result;
            cancel.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                return null;
            }

            return Parse(top, output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Ended already.
        }
    }
}
