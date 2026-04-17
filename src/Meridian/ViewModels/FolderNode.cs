using System.Collections.ObjectModel;
using Meridian.Services;

namespace Meridian.ViewModels;

public class FolderNode : ViewModelBase
{
    readonly IconCache iconCache;
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
        get;
        set
        {
            if (SetProperty(ref field, value) && value && !isLoaded)
            {
                LoadChildren();
            }
        }
    }

    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
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
