namespace MCPanel;

/// <summary>One operation per component, shared by all of its UI entry points.</summary>
internal sealed class ComponentOperationCoordinator
{
    private readonly HashSet<EnvironmentKind> _active = new();
    private readonly object _gate = new();

    public event Action<EnvironmentKind, bool>? BusyChanged;

    public IDisposable? TryBegin(EnvironmentKind kind)
    {
        lock (_gate)
        {
            if (!_active.Add(kind)) return null;
        }

        try { BusyChanged?.Invoke(kind, true); }
        catch
        {
            lock (_gate) _active.Remove(kind);
            throw;
        }
        return new Operation(this, kind);
    }

    public async Task<string?> RunAsync(
        EnvironmentKind kind,
        Func<bool> confirm,
        Func<Task<string>> execute)
    {
        using var operation = TryBegin(kind);
        if (operation is null || !confirm()) return null;
        return await execute();
    }

    private void End(EnvironmentKind kind)
    {
        lock (_gate) _active.Remove(kind);
        BusyChanged?.Invoke(kind, false);
    }

    private sealed class Operation(ComponentOperationCoordinator owner, EnvironmentKind kind) : IDisposable
    {
        private ComponentOperationCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End(kind);
    }
}
