namespace Meridian.Services;

class NavigationManager
{
    readonly Stack<string> backStack = new();
    readonly Stack<string> forwardStack = new();

    public string? CurrentPath { get; private set; }
    public bool CanGoBack => backStack.Count > 0;
    public bool CanGoForward => forwardStack.Count > 0;
    public bool CanGoUp => CurrentPath != null && Path.GetDirectoryName(CurrentPath) != null;

    public void NavigateTo(string path)
    {
        if (CurrentPath != null)
        {
            backStack.Push(CurrentPath);
        }

        forwardStack.Clear();
        CurrentPath = path;
    }

    public string? GoBack()
    {
        if (!CanGoBack)
        {
            return null;
        }

        forwardStack.Push(CurrentPath!);
        CurrentPath = backStack.Pop();
        return CurrentPath;
    }

    public string? GoForward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        backStack.Push(CurrentPath!);
        CurrentPath = forwardStack.Pop();
        return CurrentPath;
    }

    public string? GoUp()
    {
        if (!CanGoUp)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(CurrentPath!);
        if (parent == null)
        {
            return null;
        }

        backStack.Push(CurrentPath!);
        forwardStack.Clear();
        CurrentPath = parent;
        return CurrentPath;
    }
}
