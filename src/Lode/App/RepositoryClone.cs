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
    /// app it was for, as Windows did before Lode took such links over. The clone, or the file in
    /// it the link names, once cloned; otherwise null.
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
            var cancellation = new CancelSource();
            var cancel = TaskDialogButton.Cancel;

            // Git is stopped, and what it cloned so far deleted, before the dialog goes.
            cancel.AllowCloseDialog = false;
            var page = new TaskDialogPage
            {
                Caption = "Lode",
                Heading = $"Cloning {link.Name}",
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
                var url = RemoteSite.For(link.Remote)?.Url ?? link.Remote;

                // Cancel.None, not the token: Clone watches the token itself, and cancelled, ends
                // with what it cloned so far deleted. Given the token, a Cancel before the clone
                // had started would have the await throw instead, and nothing would close the
                // dialog.
                var result = await Task.Run(
                    () => Clone(url, destination, link.Branch, progress.Report, cancellation.Token),
                    Cancel.None);
                if (result.Succeeded)
                {
                    cloned = link.In(destination);
                    page.BoundDialog?.Close();
                    return;
                }

                if (cancellation.IsCancellationRequested)
                {
                    page.BoundDialog?.Close();
                    return;
                }

                page.Navigate(Failed(result.Output));
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
    internal static string Destination(string codeFolder, RepositoryLink link)
    {
        var slash = link.Name.LastIndexOf('/');
        var name = FolderName(link.Name[(slash + 1)..]);
        var top = Path.Combine(codeFolder, name);
        if (!Path.Exists(top))
        {
            return top;
        }

        if (slash > 0)
        {
            var owner = Path.Combine(codeFolder, FolderName(link.Name[..slash]));
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
    /// nothing behind. With something at <paramref name="destination"/> by now, it fails, and
    /// leaves that as it is.
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
        var madeParent = !Directory.Exists(parent);
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
            if (madeParent)
            {
                Remove(parent);
            }

            return clone;
        }

        // Where origin has no branch of the name, git refuses, and the clone is as it was.
        if (branch != null)
        {
            CodeCommands.GitToItsEnd(destination, cancel, "switch", branch);
        }

        return clone;
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
}
