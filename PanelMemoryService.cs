using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MCPanel;

internal sealed record PanelMemoryCleanupResult(
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    bool WorkingSetTrimmed);

internal static class PanelMemoryService
{
    internal static long GetWorkingSetBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    internal static PanelMemoryCleanupResult Release(bool collectManagedObjects)
    {
        var beforeBytes = GetWorkingSetBytes();

        if (collectManagedObjects)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        bool workingSetTrimmed;
        using (var process = Process.GetCurrentProcess())
        {
            workingSetTrimmed = SetProcessWorkingSetSize(
                process.Handle,
                new IntPtr(-1),
                new IntPtr(-1));
        }

        return new PanelMemoryCleanupResult(
            beforeBytes,
            GetWorkingSetBytes(),
            workingSetTrimmed);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
