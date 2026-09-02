using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MCPanel;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private bool _backgroundTipShown;
    private bool _disposed;

    public TrayIconService(Action activate, Action exit)
    {
        if (activate is null) throw new ArgumentNullException(nameof(activate));
        if (exit is null) throw new ArgumentNullException(nameof(exit));

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var openItem = new Forms.ToolStripMenuItem("打开 MCPanel");
        var exitItem = new Forms.ToolStripMenuItem("退出 MCPanel");
        openItem.Click += (_, _) => activate();
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var icon = LoadApplicationIcon();
        Forms.NotifyIcon? notifyIcon = null;
        try
        {
            notifyIcon = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "MCPanel - 后台服务运行中",
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    activate();
                }
            };
            notifyIcon.BalloonTipClicked += (_, _) => activate();
        }
        catch
        {
            notifyIcon?.Dispose();
            icon.Dispose();
            menu.Dispose();
            throw;
        }

        _menu = menu;
        _icon = icon;
        _notifyIcon = notifyIcon;
    }

    public void ShowBackgroundTip()
    {
        if (_disposed || _backgroundTipShown)
        {
            return;
        }

        _backgroundTipShown = true;
        _notifyIcon.BalloonTipTitle = "MCPanel 已转入后台";
        _notifyIcon.BalloonTipText = "主窗口资源已释放，基础服务继续运行。单击托盘图标可重新打开。";
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void Hide()
    {
        if (!_disposed)
        {
            _notifyIcon.Visible = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var executable = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(executable);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Fall back to a cloned system icon when the executable icon cannot be read.
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }
}
