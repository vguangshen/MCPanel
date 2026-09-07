using System.IO;

namespace MCPanel;

internal sealed record DriveSnapshot(string Name, long Used, long Total);
internal sealed record DriveSnapshotResult(IReadOnlyList<DriveSnapshot> Drives, bool IsComplete);

internal static class DriveSnapshotService
{
    internal static DriveSnapshotResult Capture(CancellationToken cancellationToken)
    {
        var snapshots = new List<DriveSnapshot>();
        var complete = true;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    if (!drive.IsReady) { complete = false; continue; }
                    var total = drive.TotalSize;
                    snapshots.Add(new DriveSnapshot(drive.Name, total - drive.AvailableFreeSpace, total));
                    if (snapshots.Count == 6) break;
                }
                catch (IOException) { complete = false; }
                catch (UnauthorizedAccessException) { complete = false; }
            }
        }
        catch (IOException) { complete = false; }
        catch (UnauthorizedAccessException) { complete = false; }
        return new DriveSnapshotResult(snapshots, complete);
    }
}
