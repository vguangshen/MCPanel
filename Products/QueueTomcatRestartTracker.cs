namespace MCPanel;

/// <summary>
/// Decides when the download and install queue has finished a batch that
/// needs Tomcat restarted: the queue has just emptied and at least one
/// Java/Tomcat product in that batch installed or updated successfully.
/// Failed and cancelled items do not count, and a batch with no Tomcat
/// product (IIS sites, for example) never restarts Tomcat.
/// </summary>
internal sealed class QueueTomcatRestartTracker
{
    private int _pendingTomcatProducts;

    /// <summary>Successful Tomcat products in the current batch.</summary>
    public int PendingTomcatProducts => _pendingTomcatProducts;

    /// <summary>
    /// Records a finished queue item. Returns the number of Tomcat products
    /// to restart for when this was the last active item and restarting is
    /// due; otherwise 0. The batch is reset whenever the queue empties.
    /// </summary>
    public int ItemFinished(ProductInstallQueueStatus state, bool isTomcatProduct, bool queueHasActiveItems)
    {
        if (state == ProductInstallQueueStatus.Completed && isTomcatProduct)
        {
            _pendingTomcatProducts++;
        }

        if (queueHasActiveItems)
        {
            return 0;
        }

        var due = _pendingTomcatProducts;
        _pendingTomcatProducts = 0;
        return due;
    }

    /// <summary>Same rule as the install worker: Tomcat runtime or Java products.</summary>
    public static bool IsTomcatProduct(string? runEnvironment, string? devLanguage) =>
        (runEnvironment?.IndexOf("tomcat", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
        (devLanguage?.IndexOf("java", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
}
