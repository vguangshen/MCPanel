using System.IO;
using ServiceBase = System.ServiceProcess.ServiceBase;

namespace MCPanel;

/// <summary>
/// Command line of the MCPanel.exe service hosts:
/// <c>MCPanel.exe --xxx-service "component root"</c>.
/// </summary>
internal static class ServiceHostArguments
{
    public static bool IsRequest(string[] args, string serviceArgument) =>
        Array.Exists(args, argument => string.Equals(argument, serviceArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the service when the root after <paramref name="serviceArgument"/>
    /// exists (and holds <paramref name="requiredFile"/>, if given); exit code 2 otherwise.
    /// </summary>
    public static int Run(string[] args, string serviceArgument, Func<string, ServiceBase> createService, string? requiredFile = null)
    {
        if (!TryGetRoot(args, serviceArgument, requiredFile, out var root))
        {
            return 2;
        }

        ServiceBase.Run(createService(root));
        return 0;
    }

    internal static bool TryGetRoot(string[] args, string serviceArgument, string? requiredFile, out string root)
    {
        root = string.Empty;
        var index = Array.FindIndex(args, argument => string.Equals(argument, serviceArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(args[index + 1])
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(candidate) ||
                (requiredFile is not null && !File.Exists(Path.Combine(candidate, requiredFile))))
            {
                return false;
            }

            root = candidate;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>
/// Restart budget of the in-service watchdogs (FRP, Nginx): waits 1, 3, 10,
/// 30 then 60 s between attempts, and after 5 restarts within 10 minutes
/// hands over to SCM recovery instead of looping.
/// </summary>
internal sealed class WatchdogRecoveryBudget
{
    public const int MaxRecoveriesPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    private readonly Queue<DateTime> _recoveries = new();

    public static TimeSpan GetDelay(int previousAttempts) =>
        Delays[Math.Min(Math.Max(previousAttempts, 0), Delays.Length - 1)];

    public static bool ShouldEscalate(int attemptsInWindow) => attemptsInWindow >= MaxRecoveriesPerWindow;

    /// <summary>Recoveries within the last <see cref="Window"/> as of <paramref name="now"/> (UTC).</summary>
    public int AttemptsInWindow(DateTime now)
    {
        while (_recoveries.Count > 0 && now - _recoveries.Peek() > Window)
        {
            _recoveries.Dequeue();
        }

        return _recoveries.Count;
    }

    public void Record(DateTime now) => _recoveries.Enqueue(now);
}
