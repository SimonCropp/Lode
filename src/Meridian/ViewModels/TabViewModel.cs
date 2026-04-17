using System.Collections.ObjectModel;
using System.Windows.Threading;
using Meridian.Services;

namespace Meridian.ViewModels;

public class TabViewModel : ViewModelBase
{
    readonly NavigationManager navigation = new();
    readonly IconCache iconCache;
    FileSystemWatcher? watcher;
    DispatcherTimer? refreshTimer;
    string sortColumn = "Name";
    bool sortAscending = true;

    public TabViewModel(string path, IconCache iconCache, Action<TabViewModel> closeAction)
    {
        this.iconCache = iconCache;

        BackCommand = new RelayCommand(GoBack, () => navigation.CanGoBack);
        ForwardCommand = new RelayCommand(GoForward, () => navigation.CanGoForward);
        UpCommand = new RelayCommand(GoUp, () => navigation.CanGoUp);
        CloseCommand = new RelayCommand(() => closeAction(this));
        CopyCommand = new RelayCommand(CopySelected, HasSelection);
        CutCommand = new RelayCommand(CutSelected, HasSelection);
        PasteCommand = new RelayCommand(PasteHere, ClipboardService.CanPaste);
        DeleteCommand = new RelayCommand(DeleteSelected, HasSelection);
        RenameCommand = new RelayCommand(RenameSelected, HasSingleSelection);
        NewFolderCommand = new RelayCommand(CreateNewFolder);

        LoadDrives();
        NavigateTo(path);
    }

    public ObservableCollection<FileItemViewModel> CurrentItems { get; } = [];
    public ObservableCollection<FolderNode> RootFolders { get; } = [];

    public string CurrentPath
    {
        get => navigation.CurrentPath ?? string.Empty;
        private set => OnPropertyChanged();
    }

    public string DirectoryName =>
        string.IsNullOrEmpty(CurrentPath)
            ? "New Tab"
            : Path.GetFileName(CurrentPath) is { Length: > 0 } name ? name : CurrentPath;

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand NewFolderCommand { get; }

    public int ItemCount => CurrentItems.Count;
    public long SelectedSize => CurrentItems.Where(_ => _.IsSelected).Sum(_ => _.Size);

    public void NavigateTo(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        navigation.NavigateTo(path);
        CurrentPath = path;
        OnPropertyChanged(nameof(DirectoryName));
        LoadDirectory();
        SetupWatcher(path);
    }

    public void OpenItem(FileItemViewModel item)
    {
        if (item.IsDirectory)
        {
            NavigateTo(item.FullPath);
        }
        else
        {
            Process.Start(new ProcessStartInfo(item.FullPath)
            {
                UseShellExecute = true
            });
        }
    }

    public void SortBy(string column)
    {
        if (sortColumn == column)
        {
            sortAscending = !sortAscending;
        }
        else
        {
            sortColumn = column;
            sortAscending = true;
        }

        ApplySort();
    }

    void GoBack()
    {
        if (navigation.GoBack() is { } path)
        {
            CurrentPath = path;
            OnPropertyChanged(nameof(DirectoryName));
            LoadDirectory();
            SetupWatcher(path);
        }
    }

    void GoForward()
    {
        if (navigation.GoForward() is { } path)
        {
            CurrentPath = path;
            OnPropertyChanged(nameof(DirectoryName));
            LoadDirectory();
            SetupWatcher(path);
        }
    }

    void GoUp()
    {
        if (navigation.GoUp() is { } path)
        {
            CurrentPath = path;
            OnPropertyChanged(nameof(DirectoryName));
            LoadDirectory();
            SetupWatcher(path);
        }
    }

    void LoadDirectory()
    {
        CurrentItems.Clear();
        try
        {
            var items = FileSystemService.GetDirectoryContents(CurrentPath);
            foreach (var item in items)
            {
                CurrentItems.Add(new(item, iconCache));
            }

            ApplySort();
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        OnPropertyChanged(nameof(ItemCount));
    }

    void ApplySort()
    {
        var sorted = sortColumn switch
        {
            "Name" => sortAscending
                ? CurrentItems.OrderBy(_ => !_.IsDirectory).ThenBy(_ => _.Name, StringComparer.OrdinalIgnoreCase)
                : CurrentItems.OrderBy(_ => !_.IsDirectory).ThenByDescending(_ => _.Name, StringComparer.OrdinalIgnoreCase),
            "DateModified" => sortAscending
                ? CurrentItems.OrderBy(_ => !_.IsDirectory).ThenBy(_ => _.DateModified)
                : CurrentItems.OrderBy(_ => !_.IsDirectory).ThenByDescending(_ => _.DateModified),
            "Type" => sortAscending
                ? CurrentItems.OrderBy(_ => !_.IsDirectory).ThenBy(_ => _.Extension, StringComparer.OrdinalIgnoreCase)
                : CurrentItems.OrderBy(_ => !_.IsDirectory).ThenByDescending(_ => _.Extension, StringComparer.OrdinalIgnoreCase),
            "Size" => sortAscending
                ? CurrentItems.OrderBy(_ => !_.IsDirectory).ThenBy(_ => _.Size)
                : CurrentItems.OrderBy(_ => !_.IsDirectory).ThenByDescending(_ => _.Size),
            _ => CurrentItems.AsEnumerable().OrderBy(_ => !_.IsDirectory).ThenBy(_ => _.Name, StringComparer.OrdinalIgnoreCase)
        };

        var list = sorted.ToList();
        CurrentItems.Clear();
        foreach (var item in list)
        {
            CurrentItems.Add(item);
        }
    }

    void LoadDrives()
    {
        RootFolders.Clear();
        foreach (var drive in FileSystemService.GetDrives())
        {
            RootFolders.Add(new(drive.RootDirectory.FullName, drive.Name, iconCache));
        }
    }

    void SetupWatcher(string path)
    {
        watcher?.Dispose();
        refreshTimer?.Stop();

        watcher = new(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        refreshTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            LoadDirectory();
        };

        watcher.Changed += OnFileSystemChanged;
        watcher.Created += OnFileSystemChanged;
        watcher.Deleted += OnFileSystemChanged;
        watcher.Renamed += OnFileSystemChanged;
    }

    void OnFileSystemChanged(object sender, FileSystemEventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(() => refreshTimer?.Start());

    bool HasSelection() =>
        SelectedItems.Length > 0;

    bool HasSingleSelection() =>
        SelectedItems.Length == 1;

    string[] SelectedItems =>
        CurrentItems.Where(_ => _.IsSelected).Select(_ => _.FullPath).ToArray();

    void CopySelected() =>
        ClipboardService.CopyFiles(SelectedItems);

    void CutSelected() =>
        ClipboardService.CutFiles(SelectedItems);

    void PasteHere()
    {
        ClipboardService.PasteFiles(CurrentPath);
        LoadDirectory();
    }

    void DeleteSelected()
    {
        foreach (var path in SelectedItems)
        {
            FileSystemService.Delete(path);
        }

        LoadDirectory();
    }

    void RenameSelected()
    {
        var selected = CurrentItems.FirstOrDefault(_ => _.IsSelected);
        selected?.SetEditing(true);
    }

    void CreateNewFolder()
    {
        var baseName = "New Folder";
        var name = baseName;
        var counter = 1;

        while (Directory.Exists(Path.Combine(CurrentPath, name)))
        {
            name = $"{baseName} ({counter++})";
        }

        FileSystemService.CreateDirectory(Path.Combine(CurrentPath, name));
        LoadDirectory();
    }

    public void Dispose()
    {
        watcher?.Dispose();
        refreshTimer?.Stop();
    }
}
