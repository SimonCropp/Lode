using Meridian.Models;
using Meridian.Services;

namespace Meridian.ViewModels;

public class FileItemViewModel(FileItem model, IconCache iconCache) : ViewModelBase
{
    public string Name => model.Name;
    public string FullPath => model.FullPath;
    public bool IsDirectory => model.IsDirectory;
    public long Size => model.Size;
    public DateTime DateModified => model.DateModified;
    public string Extension => model.Extension;

    public ImageSource? Icon => field ??= iconCache.GetSmallIcon(model.FullPath, model.IsDirectory);

    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsEditing
    {
        get;
        set => SetProperty(ref field, value);
    }

    public void SetEditing(bool value) =>
        IsEditing = value;
}
