using System.Windows;

namespace MCPanel;

// All calls belong to the UI dispatcher. A window can pump messages while Show
// creates its HWND, so another activation must not show that same window again.
internal sealed class WindowActivationController
{
    private bool _activating;
    internal Window? CurrentWindow { get; private set; }

    internal void Show(Func<Window> create, Action<Window> activate,
        Action<Window> discard, Action<Exception> reportError)
    {
        if (_activating) return;
        _activating = true;
        Window? window = null;
        try
        {
            window = CurrentWindow;
            if (window is null)
            {
                window = create();
                CurrentWindow = window;
                window.Closed += WindowClosed;
            }
            activate(window);
        }
        catch (Exception error)
        {
            if (window is not null)
            {
                Forget(window);
                try { discard(window); }
                catch (Exception cleanupError)
                {
                    error = new AggregateException("打开窗口失败，清理窗口时也发生异常。", error, cleanupError);
                }
            }
            // Keep the guard held while a modal error dialog pumps messages.
            reportError(error);
        }
        finally
        {
            _activating = false;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window) Forget(window);
    }

    private void Forget(Window window)
    {
        window.Closed -= WindowClosed;
        if (ReferenceEquals(CurrentWindow, window)) CurrentWindow = null;
    }
}
