using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace MCPanel
{
    internal static class Compat
    {
        public static double Clamp(double value, double minimum, double maximum) =>
            Math.Min(maximum, Math.Max(minimum, value));

        public static int Clamp(int value, int minimum, int maximum) =>
            Math.Min(maximum, Math.Max(minimum, value));

        public static string QuoteCommandLineArgument(string value)
        {
            if (value.Length > 0 && value.All(ch => !char.IsWhiteSpace(ch) && ch != '"'))
            {
                return value;
            }

            var builder = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var ch in value)
            {
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (ch == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(ch);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }

    internal static class FileCompat
    {
        public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.Run(() => File.ReadAllText(path), cancellationToken);

        public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
            Task.Run(() => File.WriteAllText(path, contents), cancellationToken);

        public static Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default) =>
            Task.Run(() => File.WriteAllText(path, contents, encoding), cancellationToken);

        public static Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken = default) =>
            Task.Run(() => File.WriteAllBytes(path, contents), cancellationToken);

        public static void Move(string source, string destination, bool overwrite)
        {
            if (overwrite && File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }

    internal static class PathCompat
    {
        public static string GetRelativePath(string relativeTo, string path)
        {
            var relativeToPath = Path.GetFullPath(relativeTo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(relativeToPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return ".";
            }

            var basePath = EnsureTrailingSeparator(relativeToPath);
            var baseUri = new Uri(basePath, UriKind.Absolute);
            var targetUri = new Uri(targetPath, UriKind.Absolute);
            if (!string.Equals(baseUri.Scheme, targetUri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return targetPath;
            }

            var relative = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }

    internal static class ParallelCompat
    {
        public static async Task ForEachAsync<T>(
            IEnumerable<T> source,
            int maximumConcurrency,
            CancellationToken cancellationToken,
            Func<T, CancellationToken, Task> action)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, maximumConcurrency));
            var tasks = source.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await action(item, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}

namespace System
{
    internal static class LegacyStringExtensions
    {
        public static bool Contains(this string value, string candidate, StringComparison comparison) =>
            value.IndexOf(candidate, comparison) >= 0;

        public static bool Contains(this string value, char candidate) =>
            value.IndexOf(candidate) >= 0;

        public static string Replace(this string value, string oldValue, string newValue, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(oldValue))
            {
                throw new ArgumentException("The value cannot be empty.", nameof(oldValue));
            }

            var index = value.IndexOf(oldValue, comparison);
            if (index < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            var start = 0;
            while (index >= 0)
            {
                builder.Append(value, start, index - start);
                builder.Append(newValue);
                start = index + oldValue.Length;
                index = value.IndexOf(oldValue, start, comparison);
            }

            builder.Append(value, start, value.Length - start);
            return builder.ToString();
        }

        public static string[] Split(this string value, char separator, StringSplitOptions options) =>
            value.Split(new[] { separator }, options);

        public static string[] Split(this string value, char separator, int count) =>
            value.Split(new[] { separator }, count, StringSplitOptions.None);

        public static bool StartsWith(this string value, char candidate) =>
            value.Length > 0 && value[0] == candidate;

        public static bool EndsWith(this string value, char candidate) =>
            value.Length > 0 && value[value.Length - 1] == candidate;
    }
}

namespace System.Linq
{
    internal static class LegacyEnumerableExtensions
    {
        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source) =>
            new(source);

        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source, IEqualityComparer<TSource> comparer) =>
            new(source, comparer);

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            IEqualityComparer<TKey>? comparer = null)
        {
            var keys = new HashSet<TKey>(comparer);
            foreach (var item in source)
            {
                if (keys.Add(keySelector(item)))
                {
                    yield return item;
                }
            }
        }

        public static IEnumerable<TSource> TakeLast<TSource>(this IEnumerable<TSource> source, int count)
        {
            if (count <= 0)
            {
                return Enumerable.Empty<TSource>();
            }

            var queue = new Queue<TSource>(count);
            foreach (var item in source)
            {
                if (queue.Count == count)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(item);
            }

            return queue;
        }
    }
}

namespace System.Collections.Generic
{
    internal static class LegacyDictionaryExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        {
            return dictionary.TryGetValue(key, out var value) ? value : default;
        }

        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        {
            return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }
}

namespace System.Diagnostics
{
    internal static class LegacyProcessExtensions
    {
        public static async Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
        {
            if (process.HasExited)
            {
                return;
            }

            var completion = new TaskCompletionSource<bool>();
            EventHandler handler = (_, _) => completion.TrySetResult(true);
            process.EnableRaisingEvents = true;
            process.Exited += handler;
            using (cancellationToken.Register(() => completion.TrySetCanceled()))
            {
                if (process.HasExited)
                {
                    completion.TrySetResult(true);
                }

                try
                {
                    await completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    process.Exited -= handler;
                }
            }
        }

        public static void Kill(this Process process, bool entireProcessTree)
        {
            if (!entireProcessTree)
            {
                process.Kill();
                return;
            }

            try
            {
                using var taskKill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {process.Id} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                taskKill?.WaitForExit(5000);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
        }
    }
}

namespace System.IO
{
    internal static class LegacyIoExtensions
    {
        public static Task<string> ReadToEndAsync(this StreamReader reader, CancellationToken cancellationToken) =>
            WaitWithCancellation(reader.ReadToEndAsync(), cancellationToken);

        public static Task WriteAsync(this Stream stream, byte[] buffer) =>
            stream.WriteAsync(buffer, 0, buffer.Length);

        public static Task<int> ReadAsync(this Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

        public static Task CopyToAsync(this Stream source, Stream destination, CancellationToken cancellationToken) =>
            source.CopyToAsync(destination, 81920, cancellationToken);

        private static async Task<T> WaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
        {
            var cancellation = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
            {
                if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task.ConfigureAwait(false);
        }
    }
}

namespace System.Threading.Tasks
{
    internal static class LegacyTaskExtensions
    {
        public static async Task WaitAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, timeoutCts.Token);
            var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (completed != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }

            timeoutCts.Cancel();
            await task.ConfigureAwait(false);
        }
    }
}

namespace System.Net.Http
{
    internal static class LegacyHttpExtensions
    {
        public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken) =>
            WaitWithCancellation(content.ReadAsStreamAsync(), cancellationToken);

        public static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken cancellationToken) =>
            WaitWithCancellation(content.ReadAsStringAsync(), cancellationToken);

        public static Task<byte[]> ReadAsByteArrayAsync(this HttpContent content, CancellationToken cancellationToken) =>
            WaitWithCancellation(content.ReadAsByteArrayAsync(), cancellationToken);

        public static async Task<Stream> GetStreamAsync(this HttpClient client, string requestUri, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        }

        private static async Task<T> WaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
        {
            var cancellation = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
            {
                if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task.ConfigureAwait(false);
        }
    }
}

namespace System.Net.Sockets
{
    internal static class LegacySocketExtensions
    {
        public static async Task ConnectAsync(this TcpClient client, string host, int port, CancellationToken cancellationToken)
        {
            var connectTask = client.ConnectAsync(host, port);
            var cancellation = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
            {
                if (connectTask != await Task.WhenAny(connectTask, cancellation.Task).ConfigureAwait(false))
                {
                    client.Close();
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            await connectTask.ConfigureAwait(false);
        }
    }
}
