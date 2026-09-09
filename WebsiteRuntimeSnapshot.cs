namespace MCPanel;

internal sealed record WebsiteRuntimeSnapshot(IisWebsiteStatusProbe Iis,
    IReadOnlyDictionary<string, TomcatProductRuntimeInfo> Tomcat)
{
    internal IReadOnlyDictionary<string, bool> Directories { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    internal static WebsiteRuntimeSnapshot Latest { get; set; } = new(IisWebsiteStatusProbe.Unavailable,
        new Dictionary<string, TomcatProductRuntimeInfo>());

    internal static WebsiteRuntimeSnapshot Capture(bool hasIis, IReadOnlyList<(string Id, int Port)> java, IReadOnlyList<string> directories, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var iis = hasIis ? IisWebsiteStatusProbe.Read() : IisWebsiteStatusProbe.Unavailable;
        token.ThrowIfCancellationRequested();
        var existence = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            existence[path] = System.IO.Directory.Exists(path);
        }
        return new(iis, TomcatProductInstanceManager.CaptureWebsiteRuntime(java, token)) { Directories = existence };
    }
}

internal sealed class WebsiteRefreshSchedule
{
    private DateTime _next;
    private bool _running;
    internal bool TryStart(DateTime now, bool visible, bool minimized, bool hasWebsites)
    {
        if (!visible || minimized || !hasWebsites || _running || now < _next) return false;
        _running = true;
        return true;
    }
    internal void Complete(DateTime now) { _running = false; _next = now.AddSeconds(30); }
    internal void RequestRefresh() { _next = DateTime.MinValue; }
}
