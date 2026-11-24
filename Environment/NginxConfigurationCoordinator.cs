using System.Text.Json;

namespace MCPanel;

/// <summary>Serializes read/modify/apply operations, including nested product sync and restart.</summary>
internal static class NginxConfigurationCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly AsyncLocal<bool> Held = new();

    // Nested operations must be awaited sequentially; do not fork work inside this scope.
    internal static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Held.Value) return await action();
        await Gate.WaitAsync(token);
        try
        {
            Held.Value = true;
            return await action();
        }
        finally
        {
            Held.Value = false;
            Gate.Release();
        }
    }

    internal static Task RunAsync(Func<Task> action, CancellationToken token = default) =>
        RunAsync(async () => { await action(); return true; }, token);

    internal static string Revision(NginxRuntimeOptions options)
    {
        var normalized = NginxRuntimeManager.NormalizeOptions(options);
        return JsonSerializer.Serialize(new
        {
            normalized.ListenPort, normalized.ProxyEnabled, normalized.ProxyTarget, normalized.Rules
        });
    }

    internal static void EnsureUnchanged(string? expectedRevision, NginxRuntimeOptions current)
    {
        if (expectedRevision is not null && expectedRevision != Revision(current))
            throw new InvalidOperationException("Nginx 配置已被其他操作更新，请关闭并重新打开管理窗口后再保存。");
    }
}
