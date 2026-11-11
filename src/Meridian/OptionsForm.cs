using System.Security;

class OptionsForm : Form
{
    readonly ExplorerReplacement explorerReplacement;
    readonly Label explorerStatus = new()
    {
        Name = "explorerStatus",
        AutoSize = true,
        MaximumSize = new(440, 0),
        Margin = new(3, 0, 3, 6)
    };
    readonly Button replaceButton = new()
    {
        Name = "replaceExplorer",
        Text = "Replace File Explorer",
        AutoSize = true,
        Padding = new(6, 0, 6, 0)
    };
    readonly Button restoreButton = new()
    {
        Name = "restoreExplorer",
        Text = "Restore File Explorer",
        AutoSize = true,
        Padding = new(6, 0, 6, 0)
    };

    readonly CheckBox showHidden = new()
    {
        Name = "showHidden",
        Text = "Show hidden items (Ctrl+H)",
        AutoSize = true,
        Margin = new(3, 3, 3, 6)
    };

    readonly CheckBox showRecent = new()
    {
        Name = "showRecent",
        Text = "Show recent folders in the folder tree",
        AutoSize = true,
        Margin = new(3, 3, 3, 0)
    };

    readonly TextBox defaultDirectory = new()
    {
        Name = "defaultDirectory",
        ReadOnly = true,
        PlaceholderText = "Your user folder",
        Width = 300,
        Margin = new(3, 4, 3, 3)
    };
    readonly Button browseDefaultDirectory = new()
    {
        Name = "browseDefaultDirectory",
        Text = "Browse...",
        AutoSize = true
    };
    readonly Button resetDefaultDirectory = new()
    {
        Name = "resetDefaultDirectory",
        Text = "Reset",
        AutoSize = true
    };

    readonly Settings settings;

    /// <summary>
    /// A setting that changes how windows look was changed.
    /// </summary>
    public event Action? SettingsChanged;

    public OptionsForm(ExplorerReplacement explorerReplacement, Settings settings)
    {
        this.explorerReplacement = explorerReplacement;
        this.settings = settings;
        browseDefaultDirectory.Click += (_, _) => BrowseDefaultDirectory();
        resetDefaultDirectory.Click += (_, _) => SetDefaultDirectory(null);
        UpdateDefaultDirectory();
        showHidden.Checked = settings.ShowHidden;
        showHidden.CheckedChanged += (_, _) =>
        {
            settings.ShowHidden = showHidden.Checked;
            SettingsChanged?.Invoke();
        };
        showRecent.Checked = settings.ShowRecent;
        showRecent.CheckedChanged += (_, _) =>
        {
            settings.ShowRecent = showRecent.Checked;
            SettingsChanged?.Invoke();
        };

        SuspendLayout();
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new(9);

        replaceButton.Click += (_, _) => Change(explorerReplacement.Replace);
        restoreButton.Click += (_, _) => Change(explorerReplacement.Restore);

        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new(3, 9, 3, 3)
        };
        AcceptButton = closeButton;
        CancelButton = closeButton;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Location = new(Padding.Left, Padding.Top)
        };
        layout.Controls.Add(BuildStartupGroup());
        layout.Controls.Add(BuildViewGroup());
        layout.Controls.Add(BuildExplorerGroup());
        layout.Controls.Add(closeButton);
        Controls.Add(layout);
        ResumeLayout(false);
        PerformLayout();

        UpdateExplorerState();
    }

    GroupBox BuildStartupGroup()
    {
        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new(420, 0),
            Margin = new(3, 3, 3, 6),
            Text = "The folder new tabs open in, and new windows given no folder to show. " +
                   "Reset goes back to your user folder."
        };

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        row.Controls.Add(defaultDirectory);
        row.Controls.Add(browseDefaultDirectory);
        row.Controls.Add(resetDefaultDirectory);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(description);
        layout.Controls.Add(row);

        var group = new GroupBox
        {
            Text = "Default folder",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new(9, 6, 9, 9)
        };
        group.Controls.Add(layout);
        return group;
    }

    void BrowseDefaultDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder new windows open in",
            UseDescriptionForTitle = true,
            InitialDirectory = settings.StartLocation()
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetDefaultDirectory(dialog.SelectedPath);
        }
    }

    void SetDefaultDirectory(string? path)
    {
        settings.DefaultDirectory = path;
        UpdateDefaultDirectory();
    }

    void UpdateDefaultDirectory()
    {
        defaultDirectory.Text = settings.DefaultDirectory ?? "";
        resetDefaultDirectory.Enabled = settings.DefaultDirectory != null;
    }

    GroupBox BuildViewGroup()
    {
        var recentDescription = new Label
        {
            AutoSize = true,
            MaximumSize = new(420, 0),
            Margin = new(21, 0, 3, 3),
            ForeColor = SystemColors.GrayText,
            Text = "Pinned folders are the ones pinned to Quick access in File Explorer. " +
                   "Recent folders are the ones Explorer adds there because you use them often."
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(showHidden);
        layout.Controls.Add(showRecent);
        layout.Controls.Add(recentDescription);

        var group = new GroupBox
        {
            Text = "View",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new(9, 6, 9, 9)
        };
        group.Controls.Add(layout);
        return group;
    }

    GroupBox BuildExplorerGroup()
    {
        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new(440, 0),
            Margin = new(3, 3, 3, 9),
            Text = "Open folders, drives and Win+E in Meridian instead of File Explorer. " +
                   "This applies to your Windows account only. Explorer's own Open stays on " +
                   "every folder's right-click menu, and the taskbar's File Explorer button " +
                   "still opens Explorer."
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(replaceButton);
        buttons.Controls.Add(restoreButton);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(description);
        layout.Controls.Add(explorerStatus);
        layout.Controls.Add(buttons);

        var group = new GroupBox
        {
            Text = "File Explorer",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new(9, 6, 9, 9)
        };
        group.Controls.Add(layout);
        return group;
    }

    void Change(Action change)
    {
        try
        {
            change();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            MessageBox.Show(this, exception.Message, "Meridian", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateExplorerState();
    }

    void UpdateExplorerState()
    {
        var registered = explorerReplacement.RegisteredCommand;
        var here = explorerReplacement.IsActiveHere;
        if (registered == null)
        {
            explorerStatus.Text = "Currently File Explorer opens them.";
        }
        else if (here)
        {
            explorerStatus.Text = "Currently Meridian opens them.";
        }
        else
        {
            explorerStatus.Text = $"Currently another copy of Meridian opens them: {registered}";
        }

        // Replace stays available when another copy is registered, to point folders at this one.
        replaceButton.Enabled = !here;
        restoreButton.Enabled = registered != null;
    }
}
