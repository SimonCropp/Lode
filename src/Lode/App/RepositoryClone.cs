/// <summary>
/// Clones the repository a link names into the code folder, when it isn't there, with the git
/// the user has installed, so their credentials and SSH keys sign in as for a clone of their
/// own. The link then opens the clone, as it would had it been there already.
/// </summary>
static class RepositoryClone
{
    /// <summary>
    /// Offers to clone the repository <paramref name="link"/> names into
    /// <paramref name="codeFolder"/>, showing how far it has got, or to hand the link on to the
    /// app it was for, as Windows did before Lode took such links over. Its submodules, where it
    /// has any, are fetched once asked, saying where they come from, as a link from a web page
    /// could name any repository, and so its submodules any url. The clone, or the file in it the
    /// link names, once cloned, its submodules fetched or not; otherwise null.
    /// </summary>
    public static string? Offer(RepositoryLink link, string codeFolder, RepositoryLinks links)
    {
        // The clone runs off this thread, and tells the dialog how far it has got, and that it
        // has ended, through this. Before any window, nothing has put one here yet; and what was
        // here is put back once the dialog has gone.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try
        {
            return Ask(link, codeFolder, links);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Each page of the dialog, once it shows. Tests click its buttons, as nobody can answer a
    /// dialog in them.
    /// </summary>
    internal static Action<TaskDialogPage>? PageShown;

    static string? Ask(RepositoryLink link, string codeFolder, RepositoryLinks links)
    {
        var destination = Destination(codeFolder, link);
        var app = link.Handler.App;
        var canForward = links.ForwardCommand(link.Handler) != null;
        var forwards = new List<TaskDialogButton>();
        string? cloned = null;

        var clone = new TaskDialogCommandLinkButton(
            "Clone it into the code folder",
            $"To {destination}, then open it in Lode.")
        {
            // The dialog stays, to show the clone going.
            AllowCloseDialog = false
        };
        var ask = new TaskDialogPage
        {
            Caption = "Lode",
            Heading = $"{link.Name} isn't cloned in the code folder",
            Text = $"Lode looked for it up to two levels into {codeFolder}.",
            AllowCancel = true,
            DefaultButton = clone,
            Buttons =
            {
                clone
            }
        };
        AddForward(ask);
        ask.Buttons.Add(TaskDialogButton.Cancel);
        ask.Created += (_, _) => PageShown?.Invoke(ask);
        clone.Click += (_, _) => ask.Navigate(Cloning());

        var answer = TaskDialog.ShowDialog(ask, TaskDialogStartupLocation.CenterScreen);
        if (forwards.Contains(answer) &&
            !link.Forward(links))
        {
            MessageBox.Show($"{app} wouldn't start.", "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return cloned;

        void AddForward(TaskDialogPage page)
        {
            if (!canForward)
            {
                return;
            }

            var forward = new TaskDialogCommandLinkButton($"Open the link in {app}");
            forwards.Add(forward);
            page.Buttons.Add(forward);
        }

        TaskDialogPage Cloning()
        {
            var url = RemoteSite.For(link.Remote)?.Url ?? link.Remote;
            return Working(
                $"Cloning {link.Name}",
                (progress, cancel) => Clone(url, destination, link.Branch, progress, cancel),
                async (page, result, cancelled) =>
                {
                    if (result.Succeeded)
                    {
                        cloned = link.In(destination);

                        // A link from a web page could name any repository, and so its
                        // submodules any url: they are fetched only once asked, saying where
                        // from.
                        var submodules = await Task.Run(() => SubmoduleUrls(destination), Cancel.None);
                        if (submodules.Count > 0)
                        {
                            page.Navigate(AskSubmodules(submodules));
                            return;
                        }

                        page.BoundDialog?.Close();
                        return;
                    }

                    if (cancelled)
                    {
                        page.BoundDialog?.Close();
                        return;
                    }

                    page.Navigate(Failed(result.Output));
                });
        }

        // The clone is there by now, and opens however this is answered, but for its submodules
        // being fetched first.
        TaskDialogPage AskSubmodules(IReadOnlyList<string> urls)
        {
            var fetch = new TaskDialogCommandLinkButton(
                "Fetch them",
                "And any they have in turn, then open it in Lode.")
            {
                // The dialog stays, to show them coming.
                AllowCloseDialog = false
            };
            var leave = new TaskDialogCommandLinkButton(
                "Open it without them",
                "Their folders stay empty: git submodule update --init --recursive fetches them later.");
            var from = string.Join(Environment.NewLine, urls.Select(_ => $"  {_}"));
            var page = new TaskDialogPage
            {
                Caption = "Lode",
                Heading = $"Fetch the submodules of {link.Name}?",
                Text = $"""
                        It is cloned, to {destination}. Its submodules come from:

                        {from}

                        A link from a web page can name any repository, so Lode asks before fetching them. Those they have in turn can't be seen until they are fetched.
                        """,
                AllowCancel = true,
                DefaultButton = fetch,
                Buttons =
                {
                    fetch,
                    leave
                }
            };
            page.Created += (_, _) => PageShown?.Invoke(page);
            fetch.Click += (_, _) => page.Navigate(FetchingSubmodules());
            return page;
        }

        // Cancelled, git is stopped, and the clone opens with what of them it fetched.
        TaskDialogPage FetchingSubmodules() =>
            Working(
                $"Fetching the submodules of {link.Name}",
                (progress, cancel) => FetchSubmodules(destination, progress, cancel),
                (page, result, cancelled) =>
                {
                    if (result.Succeeded ||
                        cancelled)
                    {
                        page.BoundDialog?.Close();
                        return Task.CompletedTask;
                    }

                    page.Navigate(SubmodulesFailed(result.Output));
                    return Task.CompletedTask;
                });

        TaskDialogPage SubmodulesFailed(string output)
        {
            var page = new TaskDialogPage
            {
                Caption = "Lode",
                Heading = $"Cloned {link.Name}, but couldn't fetch its submodules",
                Text = Reason(output),
                Icon = TaskDialogIcon.Warning,
                AllowCancel = true,
                Expander = new(output)
                {
                    CollapsedButtonText = "Show what git said",
                    ExpandedButtonText = "Hide what git said"
                },
                Buttons =
                {
                    new TaskDialogCommandLinkButton(
                        "Open it as it is",
                        "Their folders are empty: git submodule update --init --recursive fetches them once what stopped it is put right.")
                }
            };
            page.Created += (_, _) => PageShown?.Invoke(page);
            return page;
        }

        // A page showing git's progress at the work, with Cancel, which stops git, then what it
        // came to, and whether it was cancelled, given to what ends it.
        TaskDialogPage Working(string heading, Func<Action<string>, Cancel, CodeCommands.RepoResult> work, Func<TaskDialogPage, CodeCommands.RepoResult, bool, Task> ended)
        {
            var cancellation = new CancelSource();
            var cancel = TaskDialogButton.Cancel;

            // Git is stopped, and what it leaves taken care of, before the dialog goes.
            cancel.AllowCloseDialog = false;
            var page = new TaskDialogPage
            {
                Caption = "Lode",
                Heading = heading,
                Text = Showing(destination, "Starting git"),
                ProgressBar = new(TaskDialogProgressBarState.Marquee),
                AllowCancel = true,
                Buttons =
                {
                    cancel
                }
            };
            cancel.Click += (_, _) =>
            {
                cancel.Enabled = false;
                page.Text = Showing(destination, "Cancelling");
                cancellation.Cancel();
            };
            page.Created += async (_, _) =>
            {
                PageShown?.Invoke(page);
                IProgress<string> progress = new Progress<string>(line =>
                {
                    if (page.BoundDialog != null &&
                        !cancellation.IsCancellationRequested)
                    {
                        Show(page, destination, line);
                    }
                });

                // Cancel.None, not the token: the work watches the token itself, and cancelled,
                // ends having taken care of what it leaves, a clone what it cloned so far
                // deleted. Given the token, a Cancel before the work had started would have the
                // await throw instead, and nothing would close the dialog.
                var result = await Task.Run(
                    () => work(progress.Report, cancellation.Token),
                    Cancel.None);
                await ended(page, result, cancellation.IsCancellationRequested);
            };
            return page;
        }

        TaskDialogPage Failed(string output)
        {
            var page = new TaskDialogPage
            {
                Caption = "Lode",
                Heading = $"Couldn't clone {link.Name}",
                Text = Reason(output),
                Icon = TaskDialogIcon.Warning,
                AllowCancel = true,
                Expander = new(output)
                {
                    CollapsedButtonText = "Show what git said",
                    ExpandedButtonText = "Hide what git said"
                }
            };
            AddForward(page);
            page.Buttons.Add(TaskDialogButton.Close);
            page.Created += (_, _) => PageShown?.Invoke(page);
            return page;
        }
    }

    static string Showing(string destination, string line) =>
        $"To {destination}{Environment.NewLine}{Environment.NewLine}{line}";

    static void Show(TaskDialogPage page, string destination, string line)
    {
        page.Text = Showing(destination, line);
        var bar = page.ProgressBar!;
        if (Percent(line) is { } percent)
        {
            bar.State = TaskDialogProgressBarState.Normal;
            bar.Value = percent;
            return;
        }

        bar.State = TaskDialogProgressBarState.Marquee;
    }

    /// <summary>
    /// How far along the step a line of git's progress says it is: 45 for "Receiving objects:
    /// 45% (1234/2742)". Null for a line that says no percentage.
    /// </summary>
    internal static int? Percent(string line)
    {
        var sign = line.IndexOf('%');
        if (sign < 0)
        {
            return null;
        }

        var start = sign;
        while (start > 0 &&
               char.IsAsciiDigit(line[start - 1]))
        {
            start--;
        }

        if (start == sign ||
            !int.TryParse(line.AsSpan(start, sign - start), CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        return Math.Min(percent, 100);
    }

    /// <summary>
    /// Why a clone failed, in a line: git's first fatal error, which it may follow with advice,
    /// to check the url and access rights say. Otherwise the last thing it said.
    /// </summary>
    internal static string Reason(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return "Git said nothing.";
        }

        return lines.FirstOrDefault(_ => _.StartsWith("fatal:", StringComparison.Ordinal)) ?? lines[^1];
    }

    /// <summary>
    /// Where a clone of the repository <paramref name="link"/> names goes: a folder named for it
    /// at the top of <paramref name="codeFolder"/>, as GitHub Desktop names one. With that taken,
    /// by a fork of another owner say, one in a folder named for the owner, which is still near
    /// enough the top for the link to find. With that taken too, the name with a number.
    /// </summary>
    internal static string Destination(string codeFolder, RepositoryLink link) =>
        Destination(codeFolder, link.Name);

    /// <summary>
    /// Where a clone of the repository named <paramref name="fullName"/>, owner/name as its site
    /// shows it, goes, as for a link.
    /// </summary>
    internal static string Destination(string codeFolder, string fullName)
    {
        var slash = fullName.LastIndexOf('/');
        var name = FolderName(fullName[(slash + 1)..]);
        var top = Path.Combine(codeFolder, name);
        if (!Path.Exists(top))
        {
            return top;
        }

        if (slash > 0)
        {
            var owner = Path.Combine(codeFolder, FolderName(fullName[..slash]));
            var underOwner = Path.Combine(owner, name);

            // Not in another repository, where the link would not look.
            if (!File.Exists(owner) &&
                !CodeCommands.IsRepo(owner) &&
                !Path.Exists(underOwner))
            {
                return underOwner;
            }
        }

        for (var number = 2;; number++)
        {
            var numbered = $"{top}-{number}";
            if (!Path.Exists(numbered))
            {
                return numbered;
            }
        }
    }

    // A repository's name as a folder's: Azure DevOps allows characters Windows doesn't, and
    // Windows drops dots and spaces from the end of a name.
    static string FolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = name.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (invalid.Contains(characters[index]))
            {
                characters[index] = '-';
            }
        }

        var folder = new string(characters).TrimEnd('.', ' ');
        if (folder.Length == 0)
        {
            return "repository";
        }

        return folder;
    }

    /// <summary>
    /// Clones <paramref name="remote"/> into <paramref name="destination"/>, a folder not there
    /// yet, giving <paramref name="progress"/> each line of git's progress. Then switches to
    /// <paramref name="branch"/> where origin has it: a pull request's from a fork it doesn't,
    /// so the clone stays on the default branch. A clone that fails, or is cancelled, leaves
    /// nothing of its own behind: what it cloned so far, and the folders made for it, while
    /// nothing else has gone in them since. With something at <paramref name="destination"/>
    /// by now, it fails, and leaves that as it is.
    /// </summary>
    internal static CodeCommands.RepoResult Clone(string remote, string destination, string? branch, Action<string>? progress, Cancel cancel)
    {
        // Put there since the clone was offered, by another clone of the same link say. Git
        // would refuse it too, and what is taken back below when git fails would then be a
        // folder this clone never made.
        if (Path.Exists(destination))
        {
            return new(destination, false, $"{destination} is already there.");
        }

        var parent = Path.GetDirectoryName(destination)!;

        // The folders made for it, deepest first, which are all a clone that fails takes back
        // besides its own: those that weren't there already.
        var made = new List<string>();
        for (var folder = parent; folder != null && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            made.Add(folder);
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            return new(destination, false, exception.Message);
        }

        // -- so a url is never taken for an option.
        var clone = CodeCommands.Git(parent, cancel, network: true, progress, ["clone", "--progress", "--", remote, destination]) with
        {
            Repo = destination
        };
        if (!clone.Succeeded)
        {
            Remove(destination);
            TakeBack(made);
            return clone;
        }

        // Where origin has no branch of the name, git refuses, and the clone is as it was.
        if (branch != null)
        {
            CodeCommands.GitToItsEnd(destination, cancel, "switch", branch);
        }

        return AddUpstream(clone, remote, progress, cancel);
    }

    // A fork gets the repository it was made from as its upstream remote, with its default
    // branch recorded, as GitHub Desktop sets one up, so Sync fork, and how far behind upstream
    // it is, work from the first. Whatever goes wrong leaves the clone a clone that worked,
    // saying what didn't.
    static CodeCommands.RepoResult AddUpstream(CodeCommands.RepoResult clone, string remote, Action<string>? progress, Cancel cancel)
    {
        if (HostedRepos.ParentOf(remote, cancel) is not { } parent)
        {
            return clone;
        }

        var repo = clone.Repo;
        var added = CodeCommands.GitToItsEnd(repo, cancel, "remote", "add", "upstream", parent.Url);
        if (!added.Succeeded)
        {
            return Saying($"Couldn't add {parent.Url} as upstream:\n{added.Output}");
        }

        var fetch = CodeCommands.Git(repo, cancel, network: true, progress, ["fetch", "--progress", "upstream"]);
        if (!fetch.Succeeded)
        {
            return Saying($"Added {parent.Url} as upstream, but couldn't fetch it:\n{fetch.Output}");
        }

        var head = CodeCommands.Git(repo, "remote", "set-head", "upstream", parent.DefaultBranch);
        if (!head.Succeeded)
        {
            return Saying($"Added {parent.Url} as upstream, but couldn't record its default branch, {parent.DefaultBranch}:\n{head.Output}");
        }

        return Saying($"Added {parent.Url} as upstream, the repository it is a fork of");

        CodeCommands.RepoResult Saying(string text) =>
            clone with
            {
                Output = $"{clone.Output}\n{text}".Trim()
            };
    }

    /// <summary>
    /// The clone, with its submodules fetched, as GitHub Desktop's clone has them, so it builds:
    /// see <see cref="FetchSubmodules"/>. Where it has none, the clone as it was. One that can't
    /// be fetched fails the whole, as its folder is left empty, saying so, but the clone stays
    /// where it is.
    /// </summary>
    /// <param name="options">Given to git ahead of submodule: -c protocol.file.allow=always, for
    /// tests, whose submodules are file:// ones.</param>
    internal static CodeCommands.RepoResult WithSubmodules(CodeCommands.RepoResult clone, Action<string>? progress, Cancel cancel, params string[] options)
    {
        if (SubmoduleUrls(clone.Repo).Count == 0)
        {
            return clone;
        }

        var fetched = FetchSubmodules(clone.Repo, progress, cancel, options);
        return clone with
        {
            Succeeded = fetched.Succeeded,
            Output = $"{clone.Output}\n{fetched.Output}".Trim()
        };
    }

    /// <summary>
    /// Fetches the submodules of a clone, and those they have in turn, each checked out at the
    /// commit the clone has for it, as git submodule update --init --recursive does, giving
    /// <paramref name="progress"/> each line of git's progress, as the clone does. Git is given
    /// nothing of Lode's own, so it takes a submodule's url as it would in a terminal: never an
    /// ext:: one, which runs a command, nor a file:// one unless the user's config allows it, as
    /// protocol.file.allow does. What it couldn't fetch is said to be left for the user, once
    /// what stopped it is put right.
    /// </summary>
    internal static CodeCommands.RepoResult FetchSubmodules(string repo, Action<string>? progress, Cancel cancel, params string[] options)
    {
        var update = CodeCommands.Git(repo, cancel, network: true, progress, [.. options, "submodule", "update", "--init", "--recursive", "--progress"]);
        if (update.Succeeded)
        {
            return update;
        }

        return update with
        {
            Output = $"{update.Output}\nCloned, but couldn't fetch its submodules, so the folders they go in are left empty: git submodule update --init --recursive fetches them once what stopped it is put right.".Trim()
        };
    }

    /// <summary>
    /// Where the repository's submodules come from, as its .gitmodules names them and git will
    /// fetch them: one relative to origin, ../library.git say, made whole from origin's url.
    /// None where it has none.
    /// </summary>
    internal static List<string> SubmoduleUrls(string repo)
    {
        if (!File.Exists(Path.Combine(repo, ".gitmodules")))
        {
            return [];
        }

        // Each "submodule.<name>.url <url>", whatever the name holds.
        var read = CodeCommands.GitOutput(repo, Cancel.None, ["config", "--file", ".gitmodules", "--get-regexp", @"^submodule\..*\.url$"]);
        if (!read.Succeeded)
        {
            return [];
        }

        var origin = CodeCommands.GitOutput(repo, Cancel.None, ["remote", "get-url", "origin"]).Output.Trim();
        return read.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UrlOf)
            .Where(_ => _.Length > 0)
            .Select(_ => SubmoduleUrl(_, origin))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        static string UrlOf(string line)
        {
            var space = line.IndexOf(' ');
            if (space < 0)
            {
                return "";
            }

            return line[(space + 1)..].Trim();
        }
    }

    /// <summary>
    /// A submodule's url as git takes it: whole as written, or, starting ./ or ../, from
    /// <paramref name="origin"/>'s, each ../ a folder up from it, and past an ssh one's path,
    /// git@host:repository.git's, to its host.
    /// </summary>
    internal static string SubmoduleUrl(string url, string origin)
    {
        if (!url.StartsWith("./", StringComparison.Ordinal) &&
            !url.StartsWith("../", StringComparison.Ordinal))
        {
            return url;
        }

        var whole = origin.TrimEnd('/', '\\');
        var separator = '/';
        while (true)
        {
            if (url.StartsWith("./", StringComparison.Ordinal))
            {
                url = url[2..];
                continue;
            }

            if (!url.StartsWith("../", StringComparison.Ordinal))
            {
                break;
            }

            url = url[3..];
            var slash = whole.LastIndexOfAny(['/', '\\']);
            var colon = whole.LastIndexOf(':');
            if (slash > colon)
            {
                whole = whole[..slash];
            }
            else if (colon >= 0)
            {
                // What follows the host comes after its colon, as it did in origin's url.
                whole = whole[..colon];
                separator = ':';
            }
        }

        return $"{whole}{separator}{url}";
    }

    // Git takes back what it made when it fails, but not when it is stopped part way. Stopped,
    // the helpers it started may hold a file open a moment longer.
    static void Remove(string folder)
    {
        for (var attempt = 1; attempt <= 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                GitPurge.DeleteOutright(folder);
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    // Only while each is empty: by the time git fails, one may hold another clone into the same
    // owner's folder, or something saved there, and the folder goes only with nothing in it.
    static void TakeBack(List<string> made)
    {
        foreach (var folder in made)
        {
            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException)
            {
                // It stays, so the folders it is in aren't empty either.
                return;
            }
        }
    }
}
