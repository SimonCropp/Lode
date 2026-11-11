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
        Width = 300
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

    readonly TextBox codeDirectory = new()
    {
        Name = "codeDirectory",
        ReadOnly = true,
        PlaceholderText = "None",
        Width = 300
    };
    readonly Button browseCodeDirectory = new()
    {
        Name = "browseCodeDirectory",
        Text = "Browse...",
        AutoSize = true
    };
    readonly Button clearCodeDirectory = new()
    {
        Name = "clearCodeDirectory",
        Text = "Clear",
        AutoSize = true
    };

    /// <summary>
    /// Asks for the code folder. Replaced in tests, which can't answer a dialog.
    /// </summary>
    internal Func<string?> BrowseForCodeDirectory;

    readonly Settings settings;
    readonly IReadOnlyList<TextEditors.Editor> editors;

    // An entry in the text editor list. A null path is "Browse…".
    sealed record EditorItem(string Text, string? Path)
    {
        public override string ToString() => Text;
    }

    readonly ComboBox textEditor = new()
    {
        Name = "textEditor",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300
    };

    int textEditorIndex;

    /// <summary>
    /// A setting that changes how windows look was changed.
    /// </summary>
    public event Action? SettingsChanged;

    /// <param name="editors">The text editors installed, Notepad last, from
    /// <see cref="TextEditors.Find()"/>. Passed in so tests show a fixed list.</param>
    public OptionsForm(ExplorerReplacement explorerReplacement, Settings settings, IReadOnlyList<TextEditors.Editor> editors)
    {
        this.explorerReplacement = explorerReplacement;
        this.settings = settings;
        this.editors = editors;
        BrowseForEditor = ShowBrowseDialog;
        BrowseForCodeDirectory = ShowCodeDirectoryDialog;
        browseCodeDirectory.Click += (_, _) =>
        {
            if (BrowseForCodeDirectory() is { } chosen)
            {
                SetCodeDirectory(chosen);
            }
        };
        clearCodeDirectory.Click += (_, _) => SetCodeDirectory(null);
        UpdateCodeDirectory();
        FillTextEditors();
        textEditor.SelectedIndexChanged += (_, _) => TextEditorChosen();
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
        layout.Controls.Add(BuildTextEditorGroup());
        layout.Controls.Add(BuildCodeGroup());
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

        // A table row rather than a flow, so each control is centred on the row's middle and the
        // text box lines up with the taller buttons.
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        defaultDirectory.Anchor = AnchorStyles.Left;
        browseDefaultDirectory.Anchor = AnchorStyles.Left;
        resetDefaultDirectory.Anchor = AnchorStyles.Left;
        row.Controls.Add(defaultDirectory, 0, 0);
        row.Controls.Add(browseDefaultDirectory, 1, 0);
        row.Controls.Add(resetDefaultDirectory, 2, 0);

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

    GroupBox BuildCodeGroup()
    {
        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new(420, 0),
            Margin = new(3, 3, 3, 6),
            Text = "The folder your git repositories are in. Folders in it get Git pull, Git push, " +
                   "Git discard and Update NuGets on their right-click menu."
        };

        // Centred on the row's middle, as the Default folder row is.
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        codeDirectory.Anchor = AnchorStyles.Left;
        browseCodeDirectory.Anchor = AnchorStyles.Left;
        clearCodeDirectory.Anchor = AnchorStyles.Left;
        row.Controls.Add(codeDirectory, 0, 0);
        row.Controls.Add(browseCodeDirectory, 1, 0);
        row.Controls.Add(clearCodeDirectory, 2, 0);

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
            Text = "Code folder",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new(9, 6, 9, 9)
        };
        group.Controls.Add(layout);
        return group;
    }

    string? ShowCodeDirectoryDialog()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder your git repositories are in",
            UseDescriptionForTitle = true,
            InitialDirectory = settings.CodeDirectory ?? Locations.Home
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return dialog.SelectedPath;
    }

    void SetCodeDirectory(string? path)
    {
        settings.CodeDirectory = path;
        UpdateCodeDirectory();
    }

    void UpdateCodeDirectory()
    {
        codeDirectory.Text = settings.CodeDirectory ?? "";
        clearCodeDirectory.Enabled = settings.CodeDirectory != null;
    }

    GroupBox BuildTextEditorGroup()
    {
        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new(420, 0),
            Margin = new(3, 3, 3, 6),
            Text = "Edit, on the right-click menu of a text file, opens it in this editor."
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(description);
        layout.Controls.Add(textEditor);

        var group = new GroupBox
        {
            Text = "Text editor",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new(9, 6, 9, 9)
        };
        group.Controls.Add(layout);
        return group;
    }

    // The editors found, then one chosen by browsing if that is what is set, then Browse….
    void FillTextEditors()
    {
        textEditor.Items.Clear();
        foreach (var editor in editors)
        {
            textEditor.Items.Add(new EditorItem(editor.Name, editor.Path));
        }

        var chosen = settings.TextEditor;
        if (chosen != null &&
            !editors.Any(_ => string.Equals(_.Path, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            textEditor.Items.Add(new EditorItem(Path.GetFileNameWithoutExtension(chosen), chosen));
        }

        textEditor.Items.Add(new EditorItem("Browse…", null));

        // Nothing chosen yet means the first editor found, as Edit uses.
        var index = 0;
        if (chosen != null)
        {
            index = textEditor.Items
                .Cast<EditorItem>()
                .ToList()
                .FindIndex(_ => string.Equals(_.Path, chosen, StringComparison.OrdinalIgnoreCase));
        }

        textEditorIndex = Math.Max(0, index);
        textEditor.SelectedIndex = textEditorIndex;
    }

    void TextEditorChosen()
    {
        if (textEditor.SelectedIndex == textEditorIndex ||
            textEditor.SelectedItem is not EditorItem item)
        {
            return;
        }

        if (item.Path != null)
        {
            settings.TextEditor = item.Path;
            textEditorIndex = textEditor.SelectedIndex;
            return;
        }

        var browsed = BrowseForEditor();
        if (browsed == null)
        {
            // Cancelled: back to what was chosen before.
            textEditor.SelectedIndex = textEditorIndex;
            return;
        }

        settings.TextEditor = browsed;
        FillTextEditors();
    }

    /// <summary>
    /// Asks for an editor's executable. Replaced in tests, which can't answer a dialog.
    /// </summary>
    internal Func<string?> BrowseForEditor;

    string? ShowBrowseDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a text editor",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return dialog.FileName;
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
