namespace MCPanel;

/// <summary>
/// A product-download snapshot that carries enough information for the UI to
/// show real transfer feedback instead of only a percentage.
/// </summary>
public sealed record ProductDownloadProgress(
    double Percent,
    string Message,
    long BytesReceived = 0,
    long? TotalBytes = null,
    string? SpeedText = null,
    int ScannedFiles = 0,
    long ScannedBytes = 0)
{
    public string TransferText
    {
        get
        {
            var received = ProductTransferFormatting.FormatBytes(BytesReceived);
            if (ScannedFiles > 0 || ScannedBytes > 0)
            {
                var downloaded = TotalBytes is > 0
                    ? $"{received} / {ProductTransferFormatting.FormatBytes(TotalBytes.Value)}"
                    : received;
                return $"已扫描 {ScannedFiles:N0} 项 · {ProductTransferFormatting.FormatBytes(ScannedBytes)} · 已下载 {downloaded}";
            }

            return TotalBytes is > 0
                ? $"已下载 {received} / {ProductTransferFormatting.FormatBytes(TotalBytes.Value)}"
                : $"已下载 {received}";
        }
    }
}

internal static class ProductTransferFormatting
{
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0} KB/s";
        }

        return $"{Math.Max(bytesPerSecond, 0):0} B/s";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
