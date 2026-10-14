namespace MCPanel;

/// <summary>Rejects samples started before a newer refresh or component operation.</summary>
internal sealed class RuntimeRefreshCoordinator
{
    private long _generation;
    public long BeginRefresh() => Interlocked.Increment(ref _generation);
    public void Invalidate() => Interlocked.Increment(ref _generation);
    public bool IsCurrent(long generation) => generation == Interlocked.Read(ref _generation);

    public async Task<bool> RefreshAsync<T>(Func<Task<T>> collect, Action<T> apply)
    {
        var generation = BeginRefresh();
        var sample = await collect();
        if (!IsCurrent(generation)) return false;
        apply(sample);
        return true;
    }
}
