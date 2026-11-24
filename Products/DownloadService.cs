using System.IO;
using System.Net.Http;

namespace MCPanel;

internal sealed record DownloadProgressSnapshot(
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan Elapsed);

internal sealed record DownloadResult(
    long BytesReceived,
    long? TotalBytes,
    TimeSpan Elapsed)
{
    public double AverageBytesPerSecond =>
        BytesReceived / Math.Max(Elapsed.TotalSeconds, 0.001);
}

/// <summary>
/// Shared response-to-file pipeline for component, product and update
/// downloads. Callers still own HTTP request policy and package validation;
/// this class owns the byte stream, temporary file, progress and integrity
/// checks that must behave consistently.
/// </summary>
internal static class DownloadService
{
    public static async Task<DownloadResult> SaveResponseAsync(
        HttpResponseMessage response,
        string targetPath,
        Action<DownloadProgressSnapshot>? progress = null,
        DownloadPauseController? pauseController = null,
        CancellationToken cancellationToken = default,
        string temporarySuffix = ".download",
        int bufferSize = 128 * 1024,
        int progressIntervalMilliseconds = 250,
        long? expectedBytes = null,
        Action<string>? validatePartial = null,
        string? emptyFileMessage = null,
        Func<long, long, string>? incompleteFileMessage = null)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("下载目标路径不能为空。", nameof(targetPath));
        }

        if (string.IsNullOrWhiteSpace(temporarySuffix) ||
            temporarySuffix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("下载临时文件后缀无效。", nameof(temporarySuffix));
        }

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        if (progressIntervalMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(progressIntervalMilliseconds));
        }

        response.EnsureSuccessStatusCode();
        var target = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + temporarySuffix;
        var declaredLength = expectedBytes ?? response.Content.Headers.ContentLength;
        if (declaredLength is < 0)
        {
            declaredLength = null;
        }

        TryDeletePartial(partial);

        var startedAt = DateTime.UtcNow;
        var lastProgressAt = DateTime.MinValue;
        long received = 0;

        void ReportProgress(bool force)
        {
            if (progress is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force &&
                now - lastProgressAt < TimeSpan.FromMilliseconds(progressIntervalMilliseconds))
            {
                return;
            }

            lastProgressAt = now;
            var elapsed = now - startedAt;
            progress(new DownloadProgressSnapshot(
                received,
                declaredLength,
                received / Math.Max(elapsed.TotalSeconds, 0.001),
                elapsed));
        }

        try
        {
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var destination = new FileStream(
                       partial,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize,
                       useAsync: true))
            {
                var buffer = new byte[bufferSize];
                while (true)
                {
                    await WaitForPermissionAsync(pauseController, cancellationToken);
                    var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await WaitForPermissionAsync(pauseController, cancellationToken);
                    await destination.WriteAsync(buffer, 0, read, cancellationToken);
                    received += read;
                    ReportProgress(force: false);
                }

                await destination.FlushAsync(cancellationToken);
            }

            if (received <= 0)
            {
                throw new InvalidDataException(emptyFileMessage ?? "下载文件为空。");
            }

            if (declaredLength is >= 0 && received != declaredLength.Value)
            {
                var message = incompleteFileMessage?.Invoke(declaredLength.Value, received)
                    ?? $"下载文件不完整：应为 {declaredLength.Value} 字节，实际 {received} 字节。";
                throw new InvalidDataException(message);
            }

            validatePartial?.Invoke(partial);
            FileCompat.Move(partial, target, overwrite: true);
            var elapsed = DateTime.UtcNow - startedAt;
            var result = new DownloadResult(received, declaredLength, elapsed);
            ReportProgress(force: true);
            return result;
        }
        catch
        {
            TryDeletePartial(partial);
            throw;
        }
    }

    private static Task WaitForPermissionAsync(
        DownloadPauseController? pauseController,
        CancellationToken cancellationToken) =>
        pauseController?.WaitIfPausedAsync(cancellationToken) ?? Task.CompletedTask;

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The original download error is more useful to the caller.
        }
    }
}
