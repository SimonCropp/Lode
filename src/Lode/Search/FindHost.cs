/// <summary>
/// Find in Files in a window: its strip along the bottom, and the Find Results tab its results
/// are in, one a window, made the first time something is searched for. Replace runs from the
/// strip, in what Find Results shows.
/// </summary>
sealed class FindHost
{
    Form owner;
    Settings settings;
    ClosableTabControl tabs;
    Func<FolderView?> activeView;
    Func<IEnumerable<FolderView>> views;
    Action<string> showInFolder;
    TabPage? page;

    // The folder Find in Files was last opened on, which Where's folders not given in full are
    // in while Find Results, which has none, shows.
    string? folder;

    /// <param name="owner">The window the strip and Find Results are in, which Replace asks over.</param>
    /// <param name="activeView">The tab showing's view, null while there is none.</param>
    /// <param name="views">The window's folders' views, for &lt;open folders&gt;.</param>
    /// <param name="showInFolder">Show in folder, from Find Results: selects the file in a tab.</param>
    public FindHost(Form owner, Settings settings, ClosableTabControl tabs, Func<FolderView?> activeView, Func<IEnumerable<FolderView>> views, Action<string> showInFolder)
    {
        this.owner = owner;
        this.settings = settings;
        this.tabs = tabs;
        this.activeView = activeView;
        this.views = views;
        this.showInFolder = showInFolder;
        ConfirmReplace = ShowReplacePrompt;
        Panel = new(settings);
        Panel.FindRequested += Find;
        Panel.ReplaceRequested += () => _ = Replace();
        Panel.StopRequested += () => ResultsView?.Stop();
        Panel.Searching = () => ResultsView?.IsSearching == true;
        Panel.CurrentFolder = SearchableFolder;
        Panel.CloseRequested += () =>
        {
            if (ResultsShowing)
            {
                ResultsView?.FocusList();
                return;
            }

            activeView()?.FocusList();
        };
    }

    /// <summary>
    /// The strip, for the window to dock along its bottom.
    /// </summary>
    public FindPanel Panel { get; }

    /// <summary>
    /// The Find Results tab's view, while there is one.
    /// </summary>
    public FindResultsView? ResultsView { get; private set; }

    /// <summary>
    /// Whether Find Results is the tab showing.
    /// </summary>
    public bool ResultsShowing =>
        page != null &&
        tabs.SelectedTab == page;

    /// <summary>
    /// What was found so far changed while Find Results shows, for the status bar to say.
    /// </summary>
    public event Action? ProgressChanged;

    /// <summary>
    /// What the status bar says while Find Results shows: what was found so far.
    /// </summary>
    public string Progress
    {
        get
        {
            var summary = ResultsView?.Results.SummaryLines() ?? [];
            if (summary.Count > 0)
            {
                return summary[0];
            }

            return "Find Results";
        }
    }

    // The folder the tab showing has open, where Find in Files can search: not This PC, a
    // machine on the network, or a zip.
    string? SearchableFolder()
    {
        if (activeView() is { IsReadOnly: false } view)
        {
            return view.CurrentPath;
        }

        return null;
    }

    // The folders the window's tabs show, for <open folders>.
    List<string> OpenFolders() =>
        views()
            .Where(_ => !_.IsReadOnly)
            .Select(_ => _.CurrentPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Ctrl+Shift+F: the find strip, on the folder open. From Find Results, Where is left as it
    /// is.
    /// </summary>
    public void Open()
    {
        var searchable = SearchableFolder();
        if (searchable != null)
        {
            folder = searchable;
        }

        Panel.Open(searchable);
    }

    /// <summary>
    /// From the search box, whose filter matched no names: the same, searched for inside the
    /// files of the tab's folder.
    /// </summary>
    public void FindInFolder(string path, string find, bool regex)
    {
        folder = path;
        Panel.Open(path);
        Panel.SetFind(find, regex);
        Find();
    }

    /// <summary>
    /// What the strip says, searched for, in the Find Results tab, which it shows.
    /// </summary>
    public void Find()
    {
        var query = Panel.ReadQuery();
        Panel.Remember(replacing: false);
        var view = ShowResultsTab();
        var where = SearchWhere.Parse(query.Where, SearchableFolder() ?? folder, OpenFolders(), out var error);
        Matcher? matcher = null;
        if (where != null)
        {
            matcher = Matcher.Create(query, out error);
        }

        view.Run(query, where, matcher, error, settings.HiddenSystemItems());
    }

    FindResultsView ShowResultsTab()
    {
        if (ResultsView == null ||
            page == null)
        {
            ResultsView = new(settings)
            {
                Dock = DockStyle.Fill
            };
            ResultsView.StateChanged += OnResultsChanged;
            ResultsView.ShowInFolderRequested += showInFolder;
            page = new()
            {
                Padding = Padding.Empty,
                Text = "Find Results",
                ToolTipText = "Find in Files (Ctrl+Shift+F)"
            };
            page.Controls.Add(ResultsView);
            tabs.TabPages.Add(page);
        }

        tabs.SelectedTab = page;
        return ResultsView;
    }

    // The tab says while it is searching, and the status bar how far it has got.
    void OnResultsChanged()
    {
        if (page == null ||
            ResultsView == null)
        {
            return;
        }

        var text = "Find Results";
        if (ResultsView.IsSearching)
        {
            text = "Find Results…";
        }

        if (page.Text != text)
        {
            page.Text = text;
        }

        if (ResultsShowing)
        {
            ProgressChanged?.Invoke();
        }
    }

    /// <summary>
    /// A tab is closing: Find Results is let go of when it is that, and the next search makes it
    /// again.
    /// </summary>
    public void Closing(TabPage closing)
    {
        if (closing != page)
        {
            return;
        }

        page = null;
        ResultsView = null;
    }

    /// <summary>
    /// Asks whether to replace, saying how many and with what. Replaced in tests.
    /// </summary>
    internal Func<int, int, string, string, bool> ConfirmReplace;

    bool ShowReplacePrompt(int matches, int files, string find, string replacement)
    {
        var replace = new TaskDialogButton("Replace");
        var occurrences = $"{matches:N0} occurrences";
        if (matches == 1)
        {
            occurrences = "1 occurrence";
        }

        var inFiles = $"{files:N0} files";
        if (files == 1)
        {
            inFiles = "1 file";
        }

        var prompt = new TaskDialogPage
        {
            Caption = "Lode",
            Heading = $"Replace {occurrences} in {inFiles}?",
            Text = $"Each \"{find}\" becomes \"{replacement}\", in the files as they are now. Undo " +
                   "(Ctrl+Z) puts them back.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { replace, TaskDialogButton.Cancel },
            DefaultButton = replace
        };
        return TaskDialog.ShowDialog(owner, prompt) == replace;
    }

    // Whether results are of the query, and searched everything, as Replace replaces in what
    // was found.
    static bool IsFinishedFor(FindResults results, FindQuery query) =>
        results is
        {
            Done: true,
            Stopped: false,
            Error: null,
            Replaced: null
        } &&
        results.Query == query;

    /// <summary>
    /// Replace: in what Find Results shows, if it is of what the strip says now and the search
    /// finished, otherwise in what a search for that finds. Asks first, saying how many, then
    /// runs in the Actions panel, and Find Results then shows each line replaced.
    /// </summary>
    public async Task Replace()
    {
        var query = Panel.ReadQuery();
        if (ResultsView is not { IsSearching: false } view ||
            !IsFinishedFor(view.Results, query))
        {
            Find();
            view = ResultsView!;
            await view.Searching;
            if (owner.IsDisposed ||
                !IsFinishedFor(view.Results, query))
            {
                return;
            }
        }

        var results = view.Results;
        var files = results.Files.Select(_ => _.Path).ToList();
        if (files.Count == 0 ||
            Matcher.Create(query, out _) is not { } matcher)
        {
            return;
        }

        var replacement = Panel.ReplaceBox.Text;
        if (!ConfirmReplace(results.Files.Sum(_ => _.MatchCount), files.Count, query.Find, replacement))
        {
            return;
        }

        Panel.Remember(replacing: true);
        var preserveCase = Panel.PreserveCase;
        var roots = SearchWhere.Parse(query.Where, SearchableFolder() ?? folder, OpenFolders(), out _)?.Roots ?? [query.Where];
        ReplaceInFiles.Outcome? done = null;
        await ActionLog.Run(
            $"Replace \"{query.Find}\"",
            roots,
            async (progress, cancel) =>
            {
                done = await Task.Run(() => ReplaceInFiles.Run(files, matcher, replacement, preserveCase, query.ContextLines, progress, cancel), Cancel.None);
                return ReplaceOutcome(done);
            });
        if (done == null)
        {
            return;
        }

        if (done.Undo != null)
        {
            UndoHistory.Instance.Record(done.Undo);
        }

        var shown = new FindResults(query)
        {
            Done = true,
            Replaced = (replacement, done.Replaced, done.Changed.Count, done.Failed.Count)
        };
        foreach (var file in done.Changed)
        {
            shown.Add(file);
        }

        ResultsView?.ShowReplaced(shown);
    }

    static ActionLog.Outcome ReplaceOutcome(ReplaceInFiles.Outcome done)
    {
        var occurrences = $"{done.Replaced:N0} occurrences";
        if (done.Replaced == 1)
        {
            occurrences = "1 occurrence";
        }

        var summary = $"Replaced {occurrences} in {done.Changed.Count:N0} files";
        if (done.Failed.Count > 0)
        {
            summary += $", {done.Failed.Count:N0} couldn't be written";
        }

        var details = new StringBuilder();
        foreach (var failure in done.Failed)
        {
            details.AppendLine($"Not written: {failure.Path}: {failure.Error}");
        }

        foreach (var file in done.Changed)
        {
            details.AppendLine($"{file.Path}: {file.MatchCount:N0}");
        }

        return new(done.Failed.Count == 0, summary, details.ToString().TrimEnd());
    }
}
