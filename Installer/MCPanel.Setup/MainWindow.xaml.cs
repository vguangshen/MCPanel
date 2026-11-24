using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace MCPanel.Setup
{
    public partial class MainWindow : Window
    {
        private enum RunState
        {
            Idle,
            Running,
            Finished
        }

        private static readonly string[] InstallSteps = { "校验安装环境", "下载并校验程序包", "准备运行环境", "部署程序文件", "注册系统入口" };
        private static readonly string[] InstallHints =
        {
            "系统版本、PowerShell 与安装目录",
            "SHA-256 校验，确保文件未被篡改",
            "合并原配置并补齐 .NET / VC++ 运行库",
            "事务式替换，失败自动回滚",
            "卸载入口、快捷方式与目录权限"
        };
        private static readonly string[] UninstallSteps = { "停止托管组件", "清理站点与运行数据", "移除文件与系统注册" };
        private static readonly string[] UninstallHints =
        {
            "正常停止数据库、网站和 Windows 服务",
            "启动项、快捷方式与防火墙规则",
            "删除安装目录并移除卸载入口"
        };

        private readonly SetupConfig _config;
        private readonly SetupOptions _options;
        private readonly SetupLog _log;
        private readonly string _previewDirectory;
        private readonly HeroAnimator _hero;
        private readonly List<StepRow> _steps = new List<StepRow>();
        private readonly bool _uninstallMode;
        private InstallEngine _engine;
        private CancellationTokenSource _cancellation;
        private RunState _state = RunState.Idle;
        private bool _cancellable;
        private FrameworkElement _currentPage;
        private string _installedRoot;
        private bool _restartRequired;
        private int _exitCode = App.ExitCancelled;
        private double _displayedProgress;

        internal MainWindow(SetupConfig config, SetupOptions options, SetupLog log, string previewDirectory = null)
        {
            InitializeComponent();
            _config = config ?? PreviewConfig();
            _options = options;
            _log = log;
            _previewDirectory = previewDirectory;
            _uninstallMode = options.Mode == SetupMode.Uninstall;
            _hero = new HeroAnimator(this);
            VersionPill.Text = _config.Version;
            WelcomeEyebrow.Text = "MCPANEL " + _config.Version + "  ·  WINDOWS X64";
            Title = _uninstallMode ? "MCPanel · 完整卸载" : "MCPanel · 部署工作台";
            _engine = new InstallEngine(_config, _log, OnEngineProgress);
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _hero.BuildBackdrop();
            Animate(ShimmerShift, TranslateTransform.XProperty, -90, 560, 1.6, repeat: true);
            if (_previewDirectory != null)
            {
                RunPreview();
                return;
            }

            if (_uninstallMode)
            {
                PrepareUninstall();
                if (_options.Silent)
                {
                    StartUninstall();
                }

                return;
            }

            PrepareWelcome();
            if (_options.Silent)
            {
                StartInstall();
            }
        }

        // ================================================================
        // Welcome
        // ================================================================

        private void PrepareWelcome()
        {
            _hero.Idle();
            StageCaption.Text = "从环境准备到服务上线";
            PathInput.Text = string.IsNullOrWhiteSpace(_options.Directory) ? InstallEngine.DefaultInstallDirectory() : _options.Directory;
            BuildChecks();
            ShowPage(WelcomePage, WelcomeContent);
            PathInput.Focus();
            PathInput.CaretIndex = PathInput.Text.Length;
        }

        private void BuildChecks()
        {
            CheckList.Children.Clear();
            var dotNet = _engine.DotNetAvailable();
            var visualCpp = _engine.VisualCppAvailable();
            var powerShell = InstallEngine.PowerShellAvailable();
            CheckList.Children.Add(CheckRow("RuntimeGlyph", ".NET Framework 4.8", dotNet ? ChipKind.Ok : ChipKind.Auto));
            CheckList.Children.Add(CheckRow("ServiceGlyph", "VC++ 2010 SP1 x64", visualCpp ? ChipKind.Ok : ChipKind.Auto));
            CheckList.Children.Add(CheckRow("ShieldGlyph", "Windows PowerShell 3.0+", powerShell ? ChipKind.Ok : ChipKind.Blocked));
            InstallButton.IsEnabled = powerShell;
            SpaceNote.Text = "需要约 " + InstallEngine.FormatBytes(_config.RequiredSpaceBytes) + " 可用空间";
            if (!powerShell)
            {
                PathNote.Text = "请先安装 Windows Management Framework 5.1（KB3191566）并重启，然后重新运行安装程序。";
                PathNote.Foreground = (Brush)FindResource("DangerBrush");
            }
        }

        private enum ChipKind
        {
            Ok,
            Auto,
            Blocked
        }

        private FrameworkElement CheckRow(string glyph, string name, ChipKind kind)
        {
            var row = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = StepRow.Frozen("#E6EFFD") };
            icon.Child = new Path { Data = (Geometry)FindResource(glyph), Stroke = (Brush)FindResource("BrandBrush"), StrokeThickness = 1.8, Width = 14, Height = 14, Stretch = Stretch.Uniform };
            row.Children.Add(icon);
            var label = new TextBlock { Text = name, FontSize = 13, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var chip = new Border { Style = (Style)FindResource("Chip") };
            var text = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            switch (kind)
            {
                case ChipKind.Ok:
                    chip.Background = (Brush)FindResource("SuccessSoftBrush");
                    text.Foreground = (Brush)FindResource("SuccessBrush");
                    text.Text = "已满足要求";
                    break;
                case ChipKind.Auto:
                    chip.Background = StepRow.Frozen("#E6EFFD");
                    text.Foreground = (Brush)FindResource("BrandBrush");
                    text.Text = "安装时自动补齐";
                    break;
                default:
                    chip.Background = (Brush)FindResource("DangerSoftBrush");
                    text.Foreground = (Brush)FindResource("DangerBrush");
                    text.Text = "需要先安装 WMF 5.1";
                    break;
            }

            chip.Child = text;
            Grid.SetColumn(chip, 2);
            row.Children.Add(chip);
            AutomationPropertiesHelper.Name(row, name + "：" + text.Text);
            return row;
        }

        private void OnPathChanged(object sender, TextChangedEventArgs e)
        {
            if (_engine == null || !InstallEngine.PowerShellAvailable())
            {
                return;
            }

            var path = PathInput.Text;
            PathNote.Foreground = (Brush)FindResource("FaintBrush");
            if (string.IsNullOrWhiteSpace(path))
            {
                PathNote.Text = string.Empty;
                InstallButton.IsEnabled = false;
                return;
            }

            InstallButton.IsEnabled = true;
            if (InstallEngine.IsExistingInstallation(path))
            {
                PathNote.Text = "检测到已有安装：将更新程序文件，配置、数据库和站点会完整保留。";
                PathNote.Foreground = (Brush)FindResource("BrandBrush");
                InstallButton.Content = "更新 MCPanel";
            }
            else
            {
                PathNote.Text = "建议使用独立文件夹，例如 C:\\MCPanel 或 D:\\MCPanel。";
                InstallButton.Content = "开始安装";
            }
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var picked = FolderPicker.Pick(new WindowInteropHelper(this).Handle, PathInput.Text, "选择 MCPanel 安装位置");
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            // Like classic installers: picking a parent folder installs into <parent>\MCPanel.
            if (!InstallEngine.IsExistingInstallation(picked) &&
                !string.Equals(System.IO.Path.GetFileName(picked.TrimEnd('\\')), "MCPanel", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(picked) && Directory.EnumerateFileSystemEntries(picked).Any())
            {
                picked = System.IO.Path.Combine(picked, "MCPanel");
            }

            PathInput.Text = picked;
            PathInput.CaretIndex = picked.Length;
        }

        private void OnInstall(object sender, RoutedEventArgs e) => StartInstall();

        // ================================================================
        // Install run
        // ================================================================

        private void StartInstall()
        {
            if (_state == RunState.Running)
            {
                return;
            }

            var directory = PathInput.Text;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = InstallEngine.DefaultInstallDirectory();
            }

            try
            {
                directory = _engine.ValidateInstallDirectory(directory);
            }
            catch (Exception error)
            {
                PathNote.Text = error.Message;
                PathNote.Foreground = (Brush)FindResource("DangerBrush");
                Shake(PathInput);
                return;
            }

            var desktop = DesktopShortcut.IsChecked == true;
            _state = RunState.Running;
            _cancellable = true;
            _cancellation = new CancellationTokenSource();
            _displayedProgress = 0;
            var existing = InstallEngine.IsExistingInstallation(directory);
            ProgressEyebrow.Text = existing ? "正在更新" : "正在部署";
            ProgressHeading.Text = existing ? "正在更新部署工作台" : "正在准备部署工作台";
            ProgressSub.Text = "安装位置：" + directory;
            BuildSteps(InstallSteps, InstallHints);
            CancelRunButton.IsEnabled = true;
            CancelRunButton.Visibility = Visibility.Visible;
            ProgressTitle.Text = "准备中";
            ProgressDetail.Text = string.Empty;
            SetBar(0, animate: false);
            StageCaption.Text = "正在部署运行环境与服务";
            _hero.Working();
            ShowPage(ProgressPage, ProgressContent);

            var token = _cancellation.Token;
            Task.Run(() => _engine.Install(directory, _options.PackageFile, desktop, token))
                .ContinueWith(task => Dispatcher.BeginInvoke(new Action(() => OnInstallFinished(task, directory))));
        }

        private void OnEngineProgress(InstallProgress progress)
        {
            if (_previewDirectory != null)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_state != RunState.Running)
                {
                    return;
                }

                var stage = (int)progress.Stage;
                for (var index = 0; index < _steps.Count; index++)
                {
                    _steps[index].SetState(index < stage ? StepState.Done : index == stage ? StepState.Active : StepState.Pending);
                }

                if (stage >= 2) _hero.ShowModule(0);
                if (stage >= 3) _hero.ShowModule(1);
                if (stage >= 4) _hero.ShowModule(2);
                if (progress.Stage >= InstallStage.Deploy)
                {
                    // Deployment is transactional; cancelling mid-way is not offered.
                    _cancellable = false;
                    CancelRunButton.IsEnabled = false;
                }

                ProgressTitle.Text = progress.Title;
                ProgressDetail.Text = progress.Detail;
                SetBar(progress.Overall);
            }));
        }

        private void OnInstallFinished(Task<InstallResult> task, string directory)
        {
            _state = RunState.Finished;
            CancelRunButton.IsEnabled = false;
            if (task.Status == TaskStatus.RanToCompletion)
            {
                var result = task.Result;
                _installedRoot = directory;
                _restartRequired = result.RestartRequired;
                _exitCode = result.RestartRequired && _options.RestartExitCode > 0 ? _options.RestartExitCode : App.ExitSuccess;
                foreach (var step in _steps)
                {
                    step.SetState(StepState.Done);
                }

                SetBar(1);
                ShowInstallSuccess(directory, result);
                if (_options.Silent)
                {
                    DelayThen(1.6, () => CloseWith(_exitCode));
                }

                return;
            }

            var error = task.Exception?.GetBaseException();
            if (error is OperationCanceledException)
            {
                _exitCode = App.ExitCancelled;
                _state = RunState.Idle;
                PrepareWelcome();
                return;
            }

            _exitCode = App.ExitFailure;
            MarkActiveStepFailed();
            ShowFailure("安装未完成", "原程序未修改，可以安全重试。", error?.Message ?? "未知错误", retryText: "重试");
        }

        private void ShowInstallSuccess(string directory, InstallResult result)
        {
            StageCaption.Text = "部署工作台已就绪";
            _hero.Success();
            ResultEyebrow.Text = result.RestartRequired ? "需要重启" : "部署完成";
            ResultEyebrow.Foreground = (Brush)FindResource(result.RestartRequired ? "WarningBrush" : "SuccessBrush");
            ResultHeading.Text = result.RestartRequired ? "安装完成，重启后即可使用" : "部署工作台已就绪";
            ResultBody.Text = result.RestartRequired
                ? "Microsoft 运行库要求重启 Windows。重启后从桌面或开始菜单打开 MCPanel。"
                : "MCPanel " + _config.Version + " 已安装完成，现在可以开始管理运行环境、网站与服务。";
            ResultFacts.Children.Clear();
            ResultFacts.Children.Add(Fact("版本", "MCPanel " + _config.Version));
            ResultFacts.Children.Add(Fact("安装位置", directory));
            ResultFacts.Children.Add(Fact("系统入口", "开始菜单" + (DesktopShortcut.IsChecked == true ? "、桌面快捷方式" : string.Empty) + "、Windows 应用列表"));
            ResultWarning.Visibility = result.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultWarningText.Text = string.Join(Environment.NewLine, result.Warnings);
            ResultPrimary.Visibility = result.RestartRequired ? Visibility.Collapsed : Visibility.Visible;
            ResultSecondary.Content = "完成";
            ShowPage(ResultPage, ResultContent);
        }

        private FrameworkElement Fact(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = (Brush)FindResource("FaintBrush") });
            var text = new TextBlock { Text = value, FontSize = 13, Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
        }

        // ================================================================
        // Uninstall
        // ================================================================

        private void PrepareUninstall()
        {
            _hero.Removing();
            _hero.ShowModule(0, true, false);
            _hero.ShowModule(1, true, false);
            _hero.ShowModule(2, true, false);
            _hero.Idle();
            StageCaption.Text = "维护部署工作台";
            string root;
            try
            {
                root = UninstallRunner.ResolveRoot(_options.UninstallRoot);
            }
            catch (Exception error)
            {
                ShowFailure("无法开始卸载", "没有修改任何文件。", error.Message, retryText: null);
                return;
            }

            _installedRoot = root;
            UninstallLocation.Text = "安装位置：" + root;
            RemovalList.Children.Clear();
            RemovalList.Children.Add(RemovalRow("DatabaseGlyph", "数据库、站点与业务数据"));
            RemovalList.Children.Add(RemovalRow("ServiceGlyph", "托管服务、启动项与防火墙规则"));
            RemovalList.Children.Add(RemovalRow("TrashGlyph", "程序文件、快捷方式与系统注册"));
            ShowPage(ConfirmPage, ConfirmContent);
        }

        private FrameworkElement RemovalRow(string glyph, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 9) };
            row.Children.Add(new Path { Data = (Geometry)FindResource(glyph), Stroke = (Brush)FindResource("MutedBrush"), StrokeThickness = 1.6, Width = 15, Height = 15, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        private void OnConfirmUninstall(object sender, RoutedEventArgs e) => StartUninstall();

        private void StartUninstall()
        {
            if (_state == RunState.Running || _installedRoot == null)
            {
                return;
            }

            _state = RunState.Running;
            _cancellable = false;
            ProgressEyebrow.Text = "完整卸载";
            ProgressHeading.Text = "正在卸载 MCPanel";
            ProgressSub.Text = "安装位置：" + _installedRoot;
            BuildSteps(UninstallSteps, UninstallHints);
            _steps[0].SetState(StepState.Active);
            CancelRunButton.Visibility = Visibility.Collapsed;
            ProgressTitle.Text = "正在检查文件占用和安装目录";
            ProgressDetail.Text = string.Empty;
            SetBar(0.04, animate: false);
            SetBar(0.12);
            StageCaption.Text = "正在清理部署工作台";
            _hero.Removing();
            ShowPage(ProgressPage, ProgressContent);

            var root = _installedRoot;
            var runner = new UninstallRunner(_log, status => Dispatcher.BeginInvoke(new Action(() => OnUninstallStatus(status))));
            Task.Run(() => runner.Run(root))
                .ContinueWith(task => Dispatcher.BeginInvoke(new Action(() => OnUninstallFinished(task))));
        }

        private void OnUninstallStatus(string status)
        {
            if (_state != RunState.Running)
            {
                return;
            }

            ProgressTitle.Text = status;
            int stage;
            double progress;
            if (status.Contains("删除程序") || status.Contains("已完整卸载"))
            {
                stage = 2;
                progress = 0.82;
            }
            else if (status.Contains("启动项") || status.Contains("快捷方式") || status.Contains("防火墙"))
            {
                stage = 1;
                progress = 0.62;
            }
            else if (status.Contains("停止") || status.Contains("服务"))
            {
                stage = 0;
                progress = 0.36;
            }
            else
            {
                stage = 0;
                progress = Math.Max(_displayedProgress, 0.18);
            }

            for (var index = 0; index < _steps.Count; index++)
            {
                if (index < stage) _steps[index].SetState(StepState.Done);
                else if (index == stage) _steps[index].SetState(StepState.Active);
            }

            if (stage >= 1) _hero.ShowModule(0, visible: false);
            if (stage >= 2) _hero.ShowModule(1, visible: false);
            SetBar(Math.Max(_displayedProgress, progress));
        }

        private void OnUninstallFinished(Task task)
        {
            _state = RunState.Finished;
            if (task.Status == TaskStatus.RanToCompletion)
            {
                _exitCode = App.ExitSuccess;
                foreach (var step in _steps)
                {
                    step.SetState(StepState.Done);
                }

                SetBar(1);
                StageCaption.Text = "已完整卸载";
                _hero.Removed();
                ResultEyebrow.Text = "卸载完成";
                ResultEyebrow.Foreground = (Brush)FindResource("SuccessBrush");
                ResultHeading.Text = "MCPanel 已完整卸载";
                ResultBody.Text = "安装目录及其管理的服务、启动项、快捷方式和系统注册项已清理。";
                ResultFacts.Children.Clear();
                ResultFacts.Children.Add(Fact("已移除", _installedRoot));
                ResultWarning.Visibility = Visibility.Collapsed;
                ResultPrimary.Visibility = Visibility.Collapsed;
                ResultSecondary.Content = "完成";
                ShowPage(ResultPage, ResultContent);
                return;
            }

            _exitCode = App.ExitFailure;
            MarkActiveStepFailed();
            ShowFailure("卸载未完成", "卸载入口已保留，处理后可以再次重试。", task.Exception?.GetBaseException().Message ?? "未知错误", retryText: "重试完整卸载");
        }

        // ================================================================
        // Shared pages and helpers
        // ================================================================

        private void ShowFailure(string heading, string body, string message, string retryText)
        {
            StageCaption.Text = "需要处理后重试";
            _hero.Failure();
            FailureHeading.Text = heading;
            FailureBody.Text = body;
            FailureMessage.Text = message;
            RetryButton.Visibility = retryText == null ? Visibility.Collapsed : Visibility.Visible;
            RetryButton.Content = retryText ?? string.Empty;
            ShowPage(FailurePage, FailureContent);
        }

        private void OnRetry(object sender, RoutedEventArgs e)
        {
            _state = RunState.Idle;
            if (_uninstallMode)
            {
                StartUninstall();
            }
            else
            {
                PrepareWelcome();
            }
        }

        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _log.Path + "\"") { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void OnLaunch(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_installedRoot != null)
                {
                    Process.Start(new ProcessStartInfo(System.IO.Path.Combine(_installedRoot, "MCPanel.exe")) { WorkingDirectory = _installedRoot, UseShellExecute = true });
                }
            }
            catch (Exception error)
            {
                _log.Write("Launch failed: " + error.Message);
            }

            CloseWith(_exitCode);
        }

        private void OnFinish(object sender, RoutedEventArgs e) => CloseWith(_exitCode);

        private void OnCancelRun(object sender, RoutedEventArgs e)
        {
            if (_state == RunState.Running && _cancellable)
            {
                CancelRunButton.IsEnabled = false;
                ProgressDetail.Text = "正在取消，原程序不会被修改……";
                _cancellation?.Cancel();
            }
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_state != RunState.Running)
            {
                return;
            }

            e.Cancel = true;
            if (_cancellable)
            {
                OnCancelRun(sender, null);
            }
            else
            {
                ProgressDetail.Text = _uninstallMode ? "正在卸载，请等待完成后再关闭。" : "正在部署程序文件，请稍候，完成前不能关闭。";
                Shake(ProgressDetail);
            }
        }

        private void CloseWith(int code)
        {
            _exitCode = code;
            _state = RunState.Finished;
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.18));
            fade.Completed += (sender, args) => Application.Current.Shutdown(_exitCode);
            BeginAnimation(OpacityProperty, fade);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown(_exitCode);
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BuildSteps(string[] titles, string[] hints)
        {
            StepList.Children.Clear();
            _steps.Clear();
            for (var index = 0; index < titles.Length; index++)
            {
                var row = new StepRow(titles[index], hints[index], index == titles.Length - 1);
                _steps.Add(row);
                StepList.Children.Add(row);
            }
        }

        private void MarkActiveStepFailed()
        {
            var active = _steps.FirstOrDefault(step => step.State == StepState.Active) ?? _steps.FirstOrDefault(step => step.State == StepState.Pending);
            active?.SetState(StepState.Failed);
        }

        private void SetBar(double fraction, bool animate = true)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            _displayedProgress = fraction;
            var width = Math.Max(0, (Track.ActualWidth > 0 ? Track.ActualWidth : 432) * fraction);
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromSeconds(animate ? 0.45 : 0))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            ProgressPercent.Text = (int)Math.Round(fraction * 100) + "%";
            _hero.SetProgress(fraction, animate);
        }

        /// <summary>Cross-fades pages; the new page's children enter one after another.</summary>
        private void ShowPage(FrameworkElement page, Panel content)
        {
            var previous = _currentPage;
            _currentPage = page;
            if (previous != null && previous != page)
            {
                var hide = new DoubleAnimation(0, TimeSpan.FromSeconds(0.14));
                hide.Completed += (sender, args) =>
                {
                    if (_currentPage != previous)
                    {
                        previous.Visibility = Visibility.Collapsed;
                    }
                };
                previous.BeginAnimation(OpacityProperty, hide);
            }

            page.Visibility = Visibility.Visible;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)) { BeginTime = TimeSpan.FromSeconds(previous == null ? 0 : 0.1) });
            var delay = previous == null ? 0.05 : 0.14;
            var children = content.Children.OfType<UIElement>().ToList();
            var footer = (page as Grid)?.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetRow(child) == 1);
            if (footer != null)
            {
                children.Add(footer);
            }

            foreach (var child in children)
            {
                var shift = new TranslateTransform(0, 14);
                child.RenderTransform = shift;
                child.Opacity = 0;
                var begin = TimeSpan.FromSeconds(delay);
                child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35)) { BeginTime = begin });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromSeconds(0.45))
                {
                    BeginTime = begin,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                delay += 0.045;
            }
        }

        private static void Shake(UIElement element)
        {
            var shift = element.RenderTransform as TranslateTransform;
            if (shift == null || shift.IsFrozen)
            {
                shift = new TranslateTransform();
                element.RenderTransform = shift;
            }

            var shake = new DoubleAnimationUsingKeyFrames();
            var offsets = new[] { -6.0, 5, -4, 3, -1, 0 };
            for (var index = 0; index < offsets.Length; index++)
            {
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[index], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.05 * (index + 1)))));
            }

            shift.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        private static void Animate(Animatable target, DependencyProperty property, double from, double to, double seconds, bool repeat)
        {
            target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = repeat ? RepeatBehavior.Forever : new RepeatBehavior(1),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        private void DelayThen(double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        // ================================================================
        // Design preview: --render-preview <dir> renders every page to PNG.
        // ================================================================

        private static SetupConfig PreviewConfig() => new SetupConfig
        {
            Version = typeof(MainWindow).Assembly.GetName().Version.ToString(3),
            RequiredSpaceBytes = 380L * 1024 * 1024,
            MaximumRelativeLength = 80,
            DotNet = new Prerequisite { Name = ".NET Framework 4.8", MinimumRelease = 394802 },
            VisualCpp = new Prerequisite { Name = "Visual C++ 2010 SP1 x64", MinimumVersion = "10.0.40219.325" },
            ProgramFiles = new List<string>(),
            PreservedTopLevelNames = new List<string>(),
            PreservedAppSettingKeys = new List<string>(),
            UrlMigrations = new List<UrlMigration>()
        };

        private void RunPreview()
        {
            Left = SystemParameters.VirtualScreenLeft - 2000;
            ShowInTaskbar = false;
            Directory.CreateDirectory(_previewDirectory);
            var frames = new List<Tuple<string, Action, double>>
            {
                Tuple.Create<string, Action, double>("01-welcome", () =>
                {
                    _hero.Idle();
                    PathInput.Text = @"C:\MCPanel";
                    BuildPreviewChecks();
                    ShowPage(WelcomePage, WelcomeContent);
                }, 1.4),
                Tuple.Create<string, Action, double>("02-installing", () =>
                {
                    BuildSteps(InstallSteps, InstallHints);
                    ProgressEyebrow.Text = "正在部署";
                    ProgressHeading.Text = "正在准备部署工作台";
                    ProgressSub.Text = @"安装位置：C:\MCPanel";
                    _steps[0].SetState(StepState.Done, false);
                    _steps[1].SetState(StepState.Done, false);
                    _steps[2].SetState(StepState.Active, false);
                    ProgressTitle.Text = "安装必备运行库";
                    ProgressDetail.Text = "Visual C++ 2010 SP1 x64  ·  正在静默安装，请勿关闭窗口";
                    CancelRunButton.Visibility = Visibility.Visible;
                    _hero.Working();
                    _hero.ShowModule(0);
                    ShowPage(ProgressPage, ProgressContent);
                    DelayThen(0.5, () => SetBar(0.58));
                }, 2.0),
                Tuple.Create<string, Action, double>("03-installed", () =>
                {
                    DesktopShortcut.IsChecked = true;
                    ShowInstallSuccess(@"C:\MCPanel", new InstallResult());
                }, 1.6),
                Tuple.Create<string, Action, double>("04-failed", () =>
                {
                    ShowFailure("安装未完成", "原程序未修改，可以安全重试。",
                        "下载安装文件：MCPanel " + _config.Version + " 的 SHA-256 校验失败，文件可能不完整或已被替换。原程序未修改。", "重试");
                }, 1.2),
                Tuple.Create<string, Action, double>("05-uninstall-confirm", () =>
                {
                    _hero.Idle();
                    _hero.ShowModule(0, true, false);
                    _hero.ShowModule(1, true, false);
                    _hero.ShowModule(2, true, false);
                    StageCaption.Text = "维护部署工作台";
                    UninstallLocation.Text = @"安装位置：C:\MCPanel";
                    RemovalList.Children.Clear();
                    RemovalList.Children.Add(RemovalRow("DatabaseGlyph", "数据库、站点与业务数据"));
                    RemovalList.Children.Add(RemovalRow("ServiceGlyph", "托管服务、启动项与防火墙规则"));
                    RemovalList.Children.Add(RemovalRow("TrashGlyph", "程序文件、快捷方式与系统注册"));
                    ShowPage(ConfirmPage, ConfirmContent);
                }, 1.3),
                Tuple.Create<string, Action, double>("06-uninstalling", () =>
                {
                    BuildSteps(UninstallSteps, UninstallHints);
                    ProgressEyebrow.Text = "完整卸载";
                    ProgressHeading.Text = "正在卸载 MCPanel";
                    ProgressSub.Text = @"安装位置：C:\MCPanel";
                    _steps[0].SetState(StepState.Done, false);
                    _steps[1].SetState(StepState.Active, false);
                    ProgressTitle.Text = "正在清理启动项、快捷方式和防火墙规则...";
                    ProgressDetail.Text = string.Empty;
                    CancelRunButton.Visibility = Visibility.Collapsed;
                    StageCaption.Text = "正在清理部署工作台";
                    _hero.Removing();
                    _hero.ShowModule(0, visible: false);
                    ShowPage(ProgressPage, ProgressContent);
                    DelayThen(0.5, () => SetBar(0.62));
                }, 1.9),
                Tuple.Create<string, Action, double>("07-uninstalled", () =>
                {
                    _installedRoot = @"C:\MCPanel";
                    _steps.ForEach(step => step.SetState(StepState.Done, false));
                    StageCaption.Text = "已完整卸载";
                    _hero.Removed();
                    ResultEyebrow.Text = "卸载完成";
                    ResultEyebrow.Foreground = (Brush)FindResource("SuccessBrush");
                    ResultHeading.Text = "MCPanel 已完整卸载";
                    ResultBody.Text = "安装目录及其管理的服务、启动项、快捷方式和系统注册项已清理。";
                    ResultFacts.Children.Clear();
                    ResultFacts.Children.Add(Fact("已移除", @"C:\MCPanel"));
                    ResultPrimary.Visibility = Visibility.Collapsed;
                    ShowPage(ResultPage, ResultContent);
                }, 1.6)
            };

            var position = 0;
            Action next = null;
            next = () =>
            {
                if (position >= frames.Count)
                {
                    Application.Current.Shutdown(0);
                    return;
                }

                var frame = frames[position++];
                frame.Item2();
                DelayThen(frame.Item3, () =>
                {
                    SaveSnapshot(System.IO.Path.Combine(_previewDirectory, frame.Item1 + ".png"));
                    next();
                });
            };
            next();
        }

        private void BuildPreviewChecks()
        {
            CheckList.Children.Clear();
            CheckList.Children.Add(CheckRow("RuntimeGlyph", ".NET Framework 4.8", ChipKind.Ok));
            CheckList.Children.Add(CheckRow("ServiceGlyph", "VC++ 2010 SP1 x64", ChipKind.Auto));
            CheckList.Children.Add(CheckRow("ShieldGlyph", "Windows PowerShell 3.0+", ChipKind.Ok));
            SpaceNote.Text = "需要约 " + InstallEngine.FormatBytes(_config.RequiredSpaceBytes) + " 可用空间";
            PathNote.Text = "建议使用独立文件夹，例如 C:\\MCPanel 或 D:\\MCPanel。";
            InstallButton.IsEnabled = true;
        }

        private void SaveSnapshot(string file)
        {
            const double scale = 2;
            var size = new Size(Root.ActualWidth, Root.ActualHeight);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // A VisualBrush ignores Root's offset inside the window.
                context.DrawRectangle(new VisualBrush(Root), null, new Rect(size));
            }

            var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file))
            {
                encoder.Save(stream);
            }
        }
    }

    internal static class AutomationPropertiesHelper
    {
        public static void Name(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);
    }
}
