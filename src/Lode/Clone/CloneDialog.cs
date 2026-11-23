/// <summary>
/// Clone a repository, as GitHub Desktop's dialog does: from GitHub, from Azure DevOps, or from
/// any url. GitHub and Azure DevOps list the repositories the user can clone, signed in as git
/// signs in, leaving out those already in the code folder, and a filter narrows them. The clone
/// goes in a folder named for the repository in the code folder, which can be changed.
/// </summary>
sealed class CloneDialog : Form
{
    internal enum Source
    {
        GitHub,
        AzureDevOps,
        Url
    }

    // A row of the list: a heading, or a repository.
    internal sealed record Row(string Text, HostedRepo? Repo);

    Settings settings;
    string parent;
    SourceTabs sourceTabs;
    Panel listPage = new();
    Panel urlPage = new();
    FramedTextBox filterFrame = new()
    {
        Outlined = true
    };
    GlyphButton refreshButton = new();
    LinkLabel addOrganization = new();
    Label problems = new();
    Label hidden = new();
    ListBox list = new();
    FramedTextBox urlFrame = new()
    {
        Outlined = true
    };
    FramedTextBox pathFrame = new()
    {
        Outlined = true
    };
    Button chooseButton = new();
    AccentButton cloneButton = new();
    Button cancelButton = new();
    ToolTip tip = new();
    Font bold;

    Source source = Source.GitHub;
    Task<HashSet<string>> cloned;
    List<Row> rows = [];
    bool loading;
    bool pathEdited;
    bool settingPath;

    // What each site listed, while Lode runs: opening Clone again lists them at once, and
    // Refresh asks again.
    static HostedListing? gitHub;
    static HostedListing? azureDevOps;

    /// <summary>
    /// Forgets what the sites listed. For tests.
    /// </summary>
    internal static void Forget()
    {
        gitHub = null;
        azureDevOps = null;
    }

    /// <summary>
    /// The url to clone from, once Clone is pressed.
    /// </summary>
    public string Remote { get; private set; } = "";

    /// <summary>
    /// The folder to clone into, not there yet.
    /// </summary>
    public string Destination { get; private set; } = "";

    /// <summary>
    /// The repository's name, owner/name, for the Actions panel.
    /// </summary>
    public string RepoName { get; private set; } = "";

    /// <summary>
    /// What the list shows. For tests.
    /// </summary>
    internal IReadOnlyList<Row> Rows => rows;

    /// <summary>
    /// The listing and the repositories already cloned, once read. For tests to wait on.
    /// </summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    internal TextBox FilterBox => filterFrame.Box;
    internal TextBox UrlBox => urlFrame.Box;
    internal TextBox PathBox => pathFrame.Box;
    internal ListBox List => list;
    internal Button CloneButton => cloneButton;

    /// <param name="parent">Where clones go: the code folder, or without one, the folder open.</param>
    public CloneDialog(Settings settings, string parent, Source start = Source.GitHub)
    {
        this.settings = settings;
        this.parent = parent;
        var codeFolder = settings.CodeDirectory;
        cloned = Task.Run(() => HostedRepos.Cloned(codeFolder));
        bold = new(Font, FontStyle.Bold);

        Text = "Clone a repository";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(620, 600);
        BackColor = ExplorerStyle.Window;

        sourceTabs = new()
        {
            Dock = DockStyle.Top,
            Height = 40
        };
        sourceTabs.Picked += Pick;

        BuildListPage();
        BuildUrlPage();
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new(20, 16, 20, 4)
        };
        body.Controls.Add(listPage);
        body.Controls.Add(urlPage);

        Controls.Add(body);
        Controls.Add(BuildPathArea());
        Controls.Add(BuildButtons());
        Controls.Add(sourceTabs);

        AcceptButton = cloneButton;
        CancelButton = cancelButton;
        source = start;
        sourceTabs.Selected = start;
    }

    void BuildListPage()
    {
        listPage.Dock = DockStyle.Fill;
        filterFrame.Dock = DockStyle.Fill;
        filterFrame.Glyph = Icons.Search;
        filterFrame.Box.PlaceholderText = "Filter your repositories";
        filterFrame.Box.AccessibleName = "Filter repositories";
        filterFrame.Box.TextChanged += (_, _) => Refill();
        refreshButton.Text = Icons.Updates.ToString();
        refreshButton.Dock = DockStyle.Right;
        refreshButton.Width = 36;
        refreshButton.AccessibleName = "Refresh";
        refreshButton.Click += (_, _) => ListRepos(refresh: true);
        tip.SetToolTip(refreshButton, "List the repositories again");
        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = filterFrame.Height
        };
        top.Controls.Add(filterFrame);
        top.Controls.Add(new Panel
        {
            Dock = DockStyle.Right,
            Width = 8
        });
        top.Controls.Add(refreshButton);

        problems.Dock = DockStyle.Top;
        problems.AutoSize = false;
        problems.ForeColor = ExplorerStyle.ChangeColor(GitChange.Deleted);
        problems.Padding = new(0, 8, 0, 0);
        problems.Visible = false;

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.IntegralHeight = false;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.BackColor = ExplorerStyle.Window;
        list.AccessibleName = "Repositories";
        list.DrawItem += DrawRow;
        list.HandleCreated += (_, _) => SetWindowTheme(list.Handle, ExplorerStyle.ControlTheme, null);
        list.SelectedIndexChanged += (_, _) => OnPicked();
        list.DoubleClick += (_, _) =>
        {
            if (cloneButton.Enabled)
            {
                Accept();
            }
        };

        hidden.Dock = DockStyle.Bottom;
        hidden.Height = 24;
        hidden.ForeColor = ExplorerStyle.SecondaryText;
        hidden.TextAlign = ContentAlignment.MiddleLeft;

        addOrganization.Dock = DockStyle.Bottom;
        addOrganization.Height = 24;
        addOrganization.Text = "Add an organization…";
        addOrganization.TextAlign = ContentAlignment.MiddleLeft;
        addOrganization.LinkColor = ExplorerStyle.Accent;
        addOrganization.LinkClicked += (_, _) => _ = AddOrganization();

        var gap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 8
        };
        listPage.Controls.Add(list);
        listPage.Controls.Add(gap);
        listPage.Controls.Add(problems);
        listPage.Controls.Add(top);
        listPage.Controls.Add(hidden);
        listPage.Controls.Add(addOrganization);
    }

    void BuildUrlPage()
    {
        urlPage.Dock = DockStyle.Fill;
        urlPage.Visible = false;
        var label = new Label
        {
            Text = "Repository url, or a GitHub repository's owner/name",
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft
        };
        urlFrame.Dock = DockStyle.Top;
        urlFrame.Box.PlaceholderText = "https://github.com/owner/name.git";
        urlFrame.Box.AccessibleName = "Repository url";
        urlFrame.Box.TextChanged += (_, _) => OnPicked();
        urlPage.Controls.Add(urlFrame);
        urlPage.Controls.Add(label);
    }

    Control BuildPathArea()
    {
        var area = new Panel
        {
            Dock = DockStyle.Bottom,
            Padding = new(20, 4, 20, 12)
        };
        var label = new Label
        {
            Text = "Local path",
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft
        };
        pathFrame.Dock = DockStyle.Fill;
        pathFrame.Box.AccessibleName = "Local path";
        pathFrame.Box.TextChanged += (_, _) =>
        {
            if (!settingPath)
            {
                pathEdited = true;
            }

            ShowCloneButton();
        };
        chooseButton.Text = "Choose…";
        chooseButton.Dock = DockStyle.Right;
        chooseButton.Width = 96;
        chooseButton.Click += (_, _) => Choose();
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = pathFrame.Height
        };
        row.Controls.Add(pathFrame);
        row.Controls.Add(new Panel
        {
            Dock = DockStyle.Right,
            Width = 8
        });
        row.Controls.Add(chooseButton);
        area.Controls.Add(row);
        area.Controls.Add(label);
        area.Height = area.Padding.Vertical + label.Height + row.Height;
        return area;
    }

    Control BuildButtons()
    {
        var area = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new(20, 14, 20, 14)
        };
        area.Paint += (_, args) =>
        {
            using var pen = new Pen(ExplorerStyle.Divider);
            args.Graphics.DrawLine(pen, 0, 0, area.Width, 0);
        };
        cancelButton.Text = "Cancel";
        cancelButton.Dock = DockStyle.Right;
        cancelButton.Width = 120;
        cancelButton.DialogResult = DialogResult.Cancel;
        cloneButton.Text = "Clone";
        cloneButton.Dock = DockStyle.Right;
        cloneButton.Width = 120;
        cloneButton.Click += (_, _) => Accept();
        area.Controls.Add(cloneButton);
        area.Controls.Add(new Panel
        {
            Dock = DockStyle.Right,
            Width = 8
        });
        area.Controls.Add(cancelButton);
        return area;
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        Pick(source);
    }

    void Pick(Source picked)
    {
        source = picked;
        sourceTabs.Selected = picked;
        var listing = picked != Source.Url;
        listPage.Visible = listing;
        urlPage.Visible = !listing;
        addOrganization.Visible = picked == Source.AzureDevOps;
        if (listing)
        {
            ListRepos(refresh: false);
            filterFrame.Box.Focus();
        }
        else
        {
            urlFrame.Box.Focus();
        }

        OnPicked();
    }

    // Lists the site's repositories, unless they were listed already this run.
    void ListRepos(bool refresh) =>
        Loaded = LoadAsync(source, refresh);

    async Task LoadAsync(Source loadingFrom, bool refresh)
    {
        var known = Listing(loadingFrom);
        if (known != null &&
            !refresh)
        {
            await cloned;
            Refill();
            return;
        }

        loading = true;
        Refill();
        try
        {
            var alreadyCloned = await cloned;
            if (loadingFrom == Source.GitHub)
            {
                gitHub = await HostedRepos.ListGitHub(Cancel.None);
            }
            else
            {
                var organizations = settings.AzureDevOpsOrganizations
                    .Concat(HostedRepos.OrganizationsCloned(alreadyCloned))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                azureDevOps = await HostedRepos.ListAzureDevOps(organizations, Cancel.None);
            }
        }
        finally
        {
            loading = false;
        }

        if (!IsDisposed &&
            source == loadingFrom)
        {
            Refill();
        }
    }

    static HostedListing? Listing(Source from) =>
        from switch
        {
            Source.GitHub => gitHub,
            Source.AzureDevOps => azureDevOps,
            _ => null
        };

    // The rows for what is listed and what the filter matches, those cloned already left out,
    // under their headings: Your repositories first, then each owner or project.
    void Refill()
    {
        var keep = (list.SelectedItem as Row)?.Repo?.CloneUrl;
        var listing = Listing(source);
        rows = [];
        var hiddenCount = 0;
        if (loading)
        {
            rows.Add(new("Finding repositories…", null));
        }
        else if (listing != null)
        {
            var alreadyCloned = cloned.IsCompletedSuccessfully ? cloned.Result : [];
            var cloneable = listing.Repos
                .Where(_ => !HostedRepos.IsCloned(_, alreadyCloned))
                .ToList();
            hiddenCount = listing.Repos.Count - cloneable.Count;
            rows = RowsFor(CommandPalette.Match(cloneable, _ => _.Name, filterFrame.Box.Text));
        }

        problems.Text = string.Join("\n", listing?.Problems ?? []);
        problems.Visible = !loading && problems.Text.Length > 0;
        if (problems.Visible)
        {
            problems.Height = TextRenderer.MeasureText(problems.Text, problems.Font, new(Math.Max(1, listPage.Width), 0), TextFormatFlags.WordBreak).Height + problems.Padding.Vertical;
        }

        hidden.Text = "";
        if (hiddenCount == 1)
        {
            hidden.Text = "1 repository already in the code folder isn't listed";
        }
        else if (hiddenCount > 1)
        {
            hidden.Text = $"{hiddenCount:N0} repositories already in the code folder aren't listed";
        }

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var row in rows)
        {
            list.Items.Add(row);
        }

        var index = rows.FindIndex(_ => _.Repo != null && _.Repo.CloneUrl == keep);
        if (index >= 0)
        {
            list.SelectedIndex = index;
        }

        list.EndUpdate();
        OnPicked();
    }

    internal static List<Row> RowsFor(IReadOnlyList<HostedRepo> repos)
    {
        var rows = new List<Row>();
        var groups = repos
            .GroupBy(_ => _.Group)
            .OrderBy(_ => _.Key != HostedRepos.YourRepositories)
            .ThenBy(_ => _.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            rows.Add(new(group.Key, null));
            foreach (var repo in group.OrderBy(_ => _.Name, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new(repo.Name, repo));
            }
        }

        return rows;
    }

    // What would be cloned, from the repository picked or the url typed, with the folder it would
    // go in, unless that has been typed.
    void OnPicked()
    {
        if (Picked() is (_, var name) &&
            !pathEdited)
        {
            settingPath = true;
            pathFrame.Box.Text = RepositoryClone.Destination(parent, name);
            settingPath = false;
        }

        ShowCloneButton();
    }

    (string Remote, string Name)? Picked()
    {
        if (source == Source.Url)
        {
            return FromUrl(urlFrame.Box.Text);
        }

        if (list.SelectedItem is Row { Repo: { } repo })
        {
            return (repo.CloneUrl, repo.Name);
        }

        return null;
    }

    /// <summary>
    /// The url to clone, and the repository's name, from what was typed: a url, as https, ssh or
    /// git's user@host:path, or a GitHub repository's owner/name. Null for nothing typed.
    /// </summary>
    internal static (string Remote, string Name)? FromUrl(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (Regex.IsMatch(text, "^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$"))
        {
            var name = text;
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return ($"https://github.com/{name}.git", name);
        }

        if (RemoteSite.For(text) is { } site)
        {
            return (text, NameOf(site.Url));
        }

        // A url on a site Lode doesn't know: named for its last part.
        var trimmed = text.TrimEnd('/', '\\');
        var last = trimmed[(trimmed.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }

        if (last.Length == 0)
        {
            return null;
        }

        return (text, last);
    }

    // owner/name, or project/name on Azure DevOps, from a repository's page.
    static string NameOf(string page)
    {
        var parts = page.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = Uri.UnescapeDataString(parts[^1]);
        var owner = Uri.UnescapeDataString(parts[^2]);
        if (owner == "_git" &&
            parts.Length >= 3)
        {
            owner = Uri.UnescapeDataString(parts[^3]);
        }

        return $"{owner}/{name}";
    }

    void ShowCloneButton()
    {
        var path = pathFrame.Box.Text.Trim();
        var exists = false;
        var valid = false;
        try
        {
            valid = path.Length > 0 &&
                    Path.IsPathFullyQualified(path);
            exists = valid && Path.Exists(path);
        }
        catch (ArgumentException)
        {
        }

        cloneButton.Enabled = Picked() != null &&
                              valid &&
                              !exists;
        var reason = "";
        if (exists)
        {
            reason = "Something is already at the local path";
        }

        tip.SetToolTip(cloneButton, reason);
    }

    void Accept()
    {
        if (!cloneButton.Enabled ||
            Picked() is not (var remote, var name))
        {
            return;
        }

        Remote = remote;
        Destination = pathFrame.Box.Text.Trim();
        RepoName = name;
        DialogResult = DialogResult.OK;
        Close();
    }

    // A folder to clone into: the one picked, holding a folder named for the repository.
    void Choose()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where to clone the repository to",
            UseDescriptionForTitle = true,
            InitialDirectory = parent
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var folder = Path.GetFileName(pathFrame.Box.Text.Trim().TrimEnd('\\'));
        if (folder.Length == 0 &&
            Picked() is (_, var name))
        {
            folder = name[(name.LastIndexOf('/') + 1)..];
        }

        pathFrame.Box.Text = Path.Combine(dialog.SelectedPath, folder);
        pathEdited = true;
    }

    // An organization to list beside those the code folder has clones from, kept for next time.
    async Task AddOrganization()
    {
        using var form = new CodeMenu.GitTextForm(
            "Add an Azure DevOps organization",
            "Its repositories are listed too, signed in as git signs in to it.",
            "Organization:",
            "",
            "Add",
            []);
        if (await form.ShowDialogAsync(this) != DialogResult.OK ||
            IsDisposed)
        {
            return;
        }

        var organization = OrganizationOf(form.Value);
        if (organization.Length == 0)
        {
            return;
        }

        if (!settings.AzureDevOpsOrganizations.Contains(organization, StringComparer.OrdinalIgnoreCase))
        {
            settings.AzureDevOpsOrganizations.Add(organization);
            settings.Save();
        }

        Pick(Source.AzureDevOps);
        ListRepos(refresh: true);
    }

    /// <summary>
    /// The organization's name from what was typed: the name, or its page,
    /// https://dev.azure.com/name or https://name.visualstudio.com.
    /// </summary>
    internal static string OrganizationOf(string text)
    {
        text = text.Trim().TrimEnd('/');
        const string devAzure = "dev.azure.com/";
        var at = text.IndexOf(devAzure, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return text[(at + devAzure.Length)..].Split('/')[0];
        }

        var visualStudio = text.IndexOf(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
        if (visualStudio > 0)
        {
            var start = text.IndexOf("://", StringComparison.Ordinal);
            if (start >= 0)
            {
                return text[(start + 3)..visualStudio];
            }

            return text[..visualStudio];
        }

        return text.Split('/')[0];
    }

    // Up and Down in the filter move through the list, as in the branches.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (filterFrame.Box.Focused &&
            keyData is Keys.Up or Keys.Down &&
            rows.Count > 0)
        {
            var step = 1;
            if (keyData == Keys.Up)
            {
                step = -1;
            }

            var index = list.SelectedIndex;
            do
            {
                index += step;
            }
            while (index >= 0 &&
                   index < rows.Count &&
                   rows[index].Repo == null);

            if (index >= 0 &&
                index < rows.Count)
            {
                list.SelectedIndex = index;
            }

            return true;
        }

        if (keyData == (Keys.Control | Keys.Tab))
        {
            Pick((Source) (((int) source + 1) % 3));
            return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            Pick((Source) (((int) source + 2) % 3));
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        list.ItemHeight = Font.Height + LogicalToDeviceUnits(10);
    }

    void DrawRow(object? sender, DrawItemEventArgs args)
    {
        if (args.Index < 0 ||
            args.Index >= rows.Count)
        {
            return;
        }

        var row = rows[args.Index];
        var graphics = args.Graphics;
        var selected = row.Repo != null &&
                       (args.State & DrawItemState.Selected) != 0;
        var background = ExplorerStyle.Window;
        var text = list.ForeColor;
        if (selected)
        {
            background = ExplorerStyle.Blend(ExplorerStyle.Window, ExplorerStyle.Accent, 0.2f);
            if (SystemInformation.HighContrast)
            {
                background = SystemColors.Highlight;
                text = SystemColors.HighlightText;
            }
        }

        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, args.Bounds);
        }

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (row.Repo is not { } repo)
        {
            TextRenderer.DrawText(graphics, row.Text, bold, args.Bounds, ExplorerStyle.SecondaryText, flags);
            return;
        }

        var size = LogicalToDeviceUnits(16);
        var glyph = library;
        if (repo.Fork)
        {
            glyph = Icons.Branch;
        }
        else if (repo.Private)
        {
            glyph = padlock;
        }

        var iconLeft = args.Bounds.Left + LogicalToDeviceUnits(4);
        graphics.DrawImage(Icons.Bitmap(glyph, size, ExplorerStyle.SecondaryText), iconLeft, args.Bounds.Top + (args.Bounds.Height - size) / 2, size, size);
        var textBounds = Rectangle.FromLTRB(iconLeft + size + LogicalToDeviceUnits(8), args.Bounds.Top, args.Bounds.Right, args.Bounds.Bottom);
        TextRenderer.DrawText(graphics, repo.Name, list.Font, textBounds, text, flags);
    }

    // A book, as GitHub marks a repository, and a padlock for a private one.
    const char library = '';
    const char padlock = '';

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tip.Dispose();
            bold.Dispose();
        }

        base.Dispose(disposing);
    }

    // GitHub, Azure DevOps and URL along the top, the one showing underlined in the accent, as
    // GitHub Desktop's dialog has them.
    sealed class SourceTabs : Control
    {
        Source selected;
        Source? hovered;
        static readonly (Source Source, string Text)[] tabs =
        [
            (Source.GitHub, "GitHub"),
            (Source.AzureDevOps, "Azure DevOps"),
            (Source.Url, "URL")
        ];

        public event Action<Source>? Picked;

        public SourceTabs()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = ExplorerStyle.Window;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "Where from";
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Source Selected
        {
            get => selected;
            set
            {
                selected = value;
                Invalidate();
            }
        }

        Rectangle BoundsOf(int index)
        {
            var width = Width / tabs.Length;
            return new(index * width, 0, width, Height);
        }

        Source? At(Point point)
        {
            for (var index = 0; index < tabs.Length; index++)
            {
                if (BoundsOf(index).Contains(point))
                {
                    return tabs[index].Source;
                }
            }

            return null;
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            var at = At(args.Location);
            if (at != hovered)
            {
                hovered = at;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs args)
        {
            base.OnMouseLeave(args);
            hovered = null;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs args)
        {
            base.OnMouseClick(args);
            if (At(args.Location) is { } source)
            {
                Picked?.Invoke(source);
            }
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Left or Keys.Right ||
            base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (args.KeyCode == Keys.Right)
            {
                Picked?.Invoke((Source) (((int) selected + 1) % tabs.Length));
                return;
            }

            if (args.KeyCode == Keys.Left)
            {
                Picked?.Invoke((Source) (((int) selected + tabs.Length - 1) % tabs.Length));
            }
        }

        protected override void OnGotFocus(EventArgs args)
        {
            base.OnGotFocus(args);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs args)
        {
            base.OnLostFocus(args);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var graphics = args.Graphics;
            graphics.Clear(BackColor);
            using (var divider = new Pen(ExplorerStyle.Divider))
            {
                graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);
            }

            for (var index = 0; index < tabs.Length; index++)
            {
                var (source, text) = tabs[index];
                var bounds = BoundsOf(index);
                if (source == hovered)
                {
                    using var shade = new SolidBrush(ExplorerStyle.Hover(BackColor));
                    graphics.FillRectangle(shade, bounds);
                }

                if (index > 0)
                {
                    using var pen = new Pen(ExplorerStyle.Divider);
                    graphics.DrawLine(pen, bounds.Left, 0, bounds.Left, Height - 1);
                }

                TextRenderer.DrawText(graphics, text, Font, bounds, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (source == selected)
                {
                    using var accent = new SolidBrush(ExplorerStyle.Accent);
                    var line = LogicalToDeviceUnits(3);
                    graphics.FillRectangle(accent, bounds.Left, bounds.Bottom - line, bounds.Width, line);
                    if (Focused &&
                        ShowFocusCues)
                    {
                        ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(bounds, -4, -4));
                    }
                }
            }
        }
    }
}
