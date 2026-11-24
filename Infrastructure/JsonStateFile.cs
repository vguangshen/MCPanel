using System.IO;
using System.Text;
using System.Text.Json;

namespace MCPanel;

/// <summary>
/// Small JSON state files shared between the panel and its elevated workers
/// (install progress, queue envelopes): written atomically, read without
/// ever throwing because the other process may be mid-write.
/// </summary>
internal static class JsonStateFile
{
    public static void Write<T>(string path, T value) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value));

    public static bool TryRead<T>(string path, out T value)
        where T : class
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) is { } parsed)
            {
                value = parsed;
                return true;
            }
        }
        catch
        {
            // Missing, locked or half-written: the reader polls again.
        }

        value = default!;
        return false;
    }
}

/// <summary>
/// Drops repeated progress writes: the same message is written at most once
/// per interval, a new message or a forced write always goes through.
/// Not thread-safe; callers hold their own lock.
/// </summary>
internal sealed class ProgressWriteThrottle
{
    private readonly TimeSpan _interval;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private string _lastMessage = string.Empty;

    public ProgressWriteThrottle(TimeSpan interval) => _interval = interval;

    public bool ShouldWrite(string message, bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastWriteUtc < _interval && string.Equals(_lastMessage, message, StringComparison.Ordinal))
        {
            return false;
        }

        _lastWriteUtc = now;
        _lastMessage = message;
        return true;
    }
}
