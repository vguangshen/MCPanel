using System.Threading.Tasks;
using System.Windows;

namespace MCPanel;

internal static class ClipboardSafety
{
    private static readonly TimeSpan DefaultClearDelay = TimeSpan.FromMinutes(2);

    public static void ScheduleClear(string value, TimeSpan? delay = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        _ = ClearIfUnchangedAsync(value, delay ?? DefaultClearDelay);
    }

    private static async Task ClearIfUnchangedAsync(string value, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            if (Clipboard.ContainsText() && string.Equals(Clipboard.GetText(), value, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch
        {
            // Clipboard ownership can change while MCPanel is closing.
        }
    }
}
