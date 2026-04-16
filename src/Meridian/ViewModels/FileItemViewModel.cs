using System.Windows.Media;
using Meridian.Models;
using Meridian.Services;

namespace Meridian.ViewModels;

class FileItemViewModel(FileItem model, IconCache iconCache) : ViewModelBase
{
    ImageSource? icon;
    bool isSelected;
    bool isEditing;

    public string Name => model.Name;
    public string FullPath => model.FullPath;
    public bool IsDirectory => model.IsDirectory;
    public long Size => model.Size;
    public DateTime DateModified => model.DateModified;
    public string Extension => model.Extension;

    public ImageSource? Icon => icon ??= iconCache.GetSmallIcon(model.FullPath, model.IsDirectory);

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public bool IsEditing
    {
        get => isEditing;
        set => SetProperty(ref isEditing, value);
    }

    public void SetEditing(bool value) =>
        IsEditing = value;
}
