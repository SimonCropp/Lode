using System.Collections.ObjectModel;
using System.Windows.Input;
using Meridian.Services;

namespace Meridian.ViewModels;

class MainViewModel : ViewModelBase
{
    readonly IconCache iconCache = new();
    readonly string defaultPath;
    TabViewModel? activeTab;

    public MainViewModel(string? startPath = null)
    {
        defaultPath = startPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        NewTabCommand = new RelayCommand(NewTab);
        BackCommand = new RelayCommand(() => ActiveTab?.BackCommand.Execute(null), () => ActiveTab?.BackCommand.CanExecute(null) == true);
        ForwardCommand = new RelayCommand(() => ActiveTab?.ForwardCommand.Execute(null), () => ActiveTab?.ForwardCommand.CanExecute(null) == true);
        UpCommand = new RelayCommand(() => ActiveTab?.UpCommand.Execute(null), () => ActiveTab?.UpCommand.CanExecute(null) == true);

        NewTab();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public TabViewModel? ActiveTab
    {
        get => activeTab;
        set
        {
            if (SetProperty(ref activeTab, value))
            {
                OnPropertyChanged(nameof(CurrentPath));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string CurrentPath
    {
        get => ActiveTab?.CurrentPath ?? string.Empty;
        set => ActiveTab?.NavigateTo(value);
    }

    public string StatusText =>
        ActiveTab != null ? $"{ActiveTab.ItemCount} items" : string.Empty;

    public ICommand NewTabCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }

    public void NewTab() =>
        NewTab(defaultPath);

    public void NewTab(string path)
    {
        var tab = new TabViewModel(path, iconCache, CloseTab);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    public void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        tab.Dispose();
        Tabs.Remove(tab);

        if (Tabs.Count == 0)
        {
            if (ShellRegistration.IsRegisteredAsShell())
            {
                NewTab();
            }

            return;
        }

        ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public void NavigateActiveTo(string path)
    {
        ActiveTab?.NavigateTo(path);
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(StatusText));
    }
}
