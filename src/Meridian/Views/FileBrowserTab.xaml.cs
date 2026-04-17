using Meridian.Services;
using Meridian.ViewModels;

namespace Meridian.Views;

public partial class FileBrowserTab
{
    Point dragStartPoint;

    public FileBrowserTab() =>
        InitializeComponent();

    void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileItemViewModel item &&
            DataContext is TabViewModel vm)
        {
            vm.OpenItem(item);
        }
    }

    void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode folder &&
            DataContext is TabViewModel vm)
        {
            vm.NavigateTo(folder.FullPath);
        }
    }

    void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader {Column.Header: string columnName} &&
            DataContext is TabViewModel vm)
        {
            vm.SortBy(columnName);
        }
    }

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (FileItemViewModel item in e.RemovedItems)
        {
            item.IsSelected = false;
        }

        foreach (FileItemViewModel item in e.AddedItems)
        {
            item.IsSelected = true;
        }
    }

    void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not TabViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
                if (vm.DeleteCommand.CanExecute(null))
                {
                    vm.DeleteCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.F2:
                if (vm.RenameCommand.CanExecute(null))
                {
                    vm.RenameCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                if (vm.CopyCommand.CanExecute(null))
                {
                    vm.CopyCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.X when Keyboard.Modifiers == ModifierKeys.Control:
                if (vm.CutCommand.CanExecute(null))
                {
                    vm.CutCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                if (vm.PasteCommand.CanExecute(null))
                {
                    vm.PasteCommand.Execute(null);
                }

                e.Handled = true;
                break;
        }
    }

    void FileList_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        dragStartPoint = e.GetPosition(null);

    void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var diff = dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var selectedPaths = FileList.SelectedItems
            .Cast<FileItemViewModel>()
            .Select(_ => _.FullPath)
            .ToArray();

        if (selectedPaths.Length == 0)
        {
            return;
        }

        var data = new DataObject();
        var files = new StringCollection();
        files.AddRange(selectedPaths);
        data.SetFileDropList(files);
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    void FileList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not TabViewModel vm ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(vm.CurrentPath, name);

            if (string.Equals(file, dest, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isCopy)
            {
                FileSystemService.Copy(file, dest);
            }
            else
            {
                FileSystemService.Move(file, dest);
            }
        }
    }
}
