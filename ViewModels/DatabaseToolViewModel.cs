using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace MCPanel;

public sealed class DatabaseToolViewModel : ObservableObject
{
    private string _statusText = "未安装";
    private string _pathText = string.Empty;
    private string _matchedVersionText = string.Empty;
    private bool _isInstalled;
    private bool _isBusy;

    public DatabaseToolViewModel(string title, string description, string actionPrefix = "")
    {
        Title = title;
        Description = description;
        ActionPrefix = actionPrefix;
    }

    public string Title { get; }
    public string Description { get; }
    public string ActionPrefix { get; }
    public string PathActionTag => $"{ActionPrefix}Path";
    public string InstallActionTag => $"{ActionPrefix}Install";
    public string UninstallActionTag => $"{ActionPrefix}Uninstall";
    public string ConnectActionTag => $"{ActionPrefix}Connect";
    public string ConfigureActionTag => $"{ActionPrefix}Configure";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string PathText { get => _pathText; private set => SetProperty(ref _pathText, value); }
    public string MatchedVersionText { get => _matchedVersionText; private set => SetProperty(ref _matchedVersionText, value); }

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (!SetProperty(ref _isInstalled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanOpenPath));
        }
    }

    public bool CanInstall => !IsBusy && !IsInstalled;
    public bool CanUninstall => !IsBusy && IsInstalled;
    public bool CanConnect => !IsBusy && IsInstalled;
    public bool CanConfigure => !IsBusy;
    public bool CanOpenPath => !IsBusy;

    internal void Apply(DatabaseToolInstallation? installation, string managedInstallRoot, string? matchedVersion = null)
    {
        IsInstalled = installation is not null;
        MatchedVersionText = string.IsNullOrWhiteSpace(matchedVersion)
            ? string.Empty
            : $"（{matchedVersion}）";
        StatusText = installation is null
            ? "未安装"
            : "已安装";
        if (installation is null)
        {
            PathText = $"未在桌面或默认安装位置检测到；面板安装目录：{managedInstallRoot}";
        }
        else if (installation.Source == DatabaseToolSource.Desktop &&
                 !string.Equals(installation.DiscoveryPath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            PathText = $"桌面快捷方式：{installation.DiscoveryPath}  →  {installation.ExecutablePath}";
        }
        else
        {
            PathText = $"{installation.SourceText}：{installation.ExecutablePath}";
        }

        IsBusy = false;
    }

    internal void SetBusy(bool busy)
    {
        IsBusy = busy;
    }

    internal void SetProgressMessage(string message)
    {
        StatusText = "处理中";
        PathText = message;
    }
}
