using System.Diagnostics;

namespace MCPanel;

internal static class ProcessLifecycle
{
    public static async Task WaitForExitAsync(
        Process process,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var waitTask = process.WaitForExitAsync(cancellationToken);
        if (timeout is null)
        {
            try
            {
                await waitTask;
                return;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }

        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout.Value));
        if (completed != waitTask)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"进程在 {timeout.Value.TotalSeconds:0} 秒内没有退出，已停止该进程。");
        }

        try
        {
            await waitTask;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // The original cancellation/timeout should remain the visible error.
        }
    }
}
