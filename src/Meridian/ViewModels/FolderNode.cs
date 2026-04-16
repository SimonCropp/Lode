using System.Collections.ObjectModel;
using System.Windows.Media;
using Meridian.Services;

namespace Meridian.ViewModels;

class FolderNode : ViewModelBase
{
    readonly IconCache iconCache;
    bool isExpanded;
    bool isSelected;
    bool isLoaded;

    public FolderNode(string path, string name, IconCache iconCache)
    {
        FullPath = path;
        Name = name;
        this.iconCache = iconCache;

        if (path.Length > 0)
        {
            Children.Add(new(string.Empty, "Loading...", iconCache));
        }
    }

    public string FullPath { get; }
    public string Name { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public ImageSource? Icon => iconCache.GetSmallIcon(FullPath, true);

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (SetProperty(ref isExpanded, value) && value && !isLoaded)
            {
                LoadChildren();
            }
        }
    }

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    void LoadChildren()
    {
        isLoaded = true;
        Children.Clear();

        try
        {
            var dir = new DirectoryInfo(FullPath);
            foreach (var sub in dir.EnumerateDirectories())
            {
                if ((sub.Attributes & FileAttributes.Hidden) != 0 ||
                    (sub.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                Children.Add(new(sub.FullName, sub.Name, iconCache));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }
}
