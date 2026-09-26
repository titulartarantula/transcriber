namespace Transcriber.Mobile;

/// <summary>Tracks whether the app is on screen, and lets background work wait until it is.</summary>
public static class AppLifecycle
{
    private static TaskCompletionSource _foreground = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static bool IsForeground { get; private set; }

    /// <summary>Hooks a window's lifecycle. Called for every window, including after Android recreates the activity.</summary>
    public static void Attach(Window window)
    {
        window.Created += (_, _) => Set(true);
        window.Activated += (_, _) => Set(true);
        window.Resumed += (_, _) => Set(true);
        window.Stopped += (_, _) => Set(false);
        window.Destroying += (_, _) => Set(false);
    }

    public static Task WaitForForegroundAsync(CancellationToken ct) =>
        IsForeground ? Task.CompletedTask : _foreground.Task.WaitAsync(ct);

    private static void Set(bool foreground)
    {
        IsForeground = foreground;
        if (foreground)
            _foreground.TrySetResult();
        else if (_foreground.Task.IsCompleted)
            _foreground = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
