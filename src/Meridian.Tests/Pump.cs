using System.Runtime.CompilerServices;

/// <summary>
/// Tests have no message loop. This stands in for one while waiting on the app's background
/// work: it dispatches window messages (timers, posted callbacks) and yields so awaited
/// continuations can run.
/// </summary>
static class Pump
{
    public static async Task Until(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string description = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {description}");
            }

            Application.DoEvents();
            await Task.Delay(15);
        }

        Application.DoEvents();
    }

    public static async Task Loaded(FolderView view)
    {
        await Until(() => view.Loading.IsCompleted);

        // Surfaces a failed load as the failure, rather than as an oddly empty list later.
        await view.Loading;
    }
}
