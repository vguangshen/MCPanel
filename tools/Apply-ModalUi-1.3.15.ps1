$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    $full = Join-Path $repoRoot $Path
    $dir = Split-Path -Parent $full
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($full, $Text.TrimStart("`r", "`n") + "`n", $utf8NoBom)
}

function Replace-Once([string]$Text, [string]$Old, [string]$New, [string]$Description) {
    $first = $Text.IndexOf($Old, [System.StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Unable to locate $Description." }
    $second = $Text.IndexOf($Old, $first + $Old.Length, [System.StringComparison]::Ordinal)
    if ($second -ge 0) { throw "Expected one match for $Description, found multiple." }
    $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

function Replace-RegexOnce([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Description) {
    $regex = [regex]::new($Pattern)
    $matches = $regex.Matches($Text)
    if ($matches.Count -ne 1) { throw "Expected one regex match for $Description, found $($matches.Count)." }
    $regex.Replace($Text, $Replacement, 1)
}

Write-Host '1/12 Creating shared modal window infrastructure...'
Write-RepoText 'PanelModalWindow.cs' @'
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MCPanel;

public class PanelModalWindow : Window
{
    public const double StandardCardWidth = 820d;
    public const double StandardCardHeight = 600d;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    public PanelModalWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromArgb(0x78, 0x00, 0x00, 0x00));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => FitOverlayToOwner();
        Loaded += (_, _) => FitOverlayToOwner();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FitOverlayToOwner();
    }

    private void FitOverlayToOwner()
    {
        if (Owner is not null && TryGetWindowBounds(Owner, out var bounds))
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(StandardCardWidth + 24d, bounds.Width);
            Height = Math.Max(StandardCardHeight + 24d, bounds.Height);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(workArea.Width, Math.Max(StandardCardWidth + 40d, 1024d));
        Height = Math.Min(workArea.Height, Math.Max(StandardCardHeight + 40d, 700d));
        Left = workArea.Left + Math.Max(0d, (workArea.Width - Width) / 2d);
        Top = workArea.Top + Math.Max(0d, (workArea.Height - Height) / 2d);
    }

    private static bool TryGetWindowBounds(Window window, out Rect bounds)
    {
        bounds = Rect.Empty;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
            {
                var source = PresentationSource.FromVisual(window);
                var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
                var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
                var width = bottomRight.X - topLeft.X;
                var height = bottomRight.Y - topLeft.Y;
                if (width > 0d && height > 0d)
                {
                    bounds = new Rect(topLeft, bottomRight);
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to WPF dimensions below.
        }

        var widthFallback = window.ActualWidth > 0d ? window.ActualWidth : window.Width;
        var heightFallback = window.ActualHeight > 0d ? window.ActualHeight : window.Height;
        if (double.IsNaN(widthFallback) || double.IsNaN(heightFallback) || widthFallback <= 0d || heightFallback <= 0d)
        {
            return false;
        }

        bounds = new Rect(window.Left, window.Top, widthFallback, heightFallback);
        return true;
    }
}
'@

Write-RepoText 'Resources/PanelModalChrome.xaml' @'
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="PanelModalSurface" TargetType="Border">
        <Setter Property="Width" Value="820" />
        <Setter Property="Height" Value="600" />
        <Setter Property="HorizontalAlignment" Value="Center" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Background" Value="{DynamicResource DialogSurfaceBrush}" />
        <Setter Property="BorderBrush" Value="{DynamicResource DialogLineBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="14" />
        <Setter Property="Padding" Value="24" />
        <Setter Property="Effect">
            <Setter.Value>
                <DropShadowEffect BlurRadius="34" ShadowDepth="9" Opacity="0.34" Color="#000000" />
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="PanelModalContentCard" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource SurfaceAltBrush}" />
        <Setter Property="BorderBrush" Value="{DynamicResource DialogLineBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="10" />
        <Setter Property="Padding" Value="18" />
    </Style>

    <Style x:Key="PanelModalIconBadge" TargetType="Border">
        <Setter Property="Width" Value="46" />
        <Setter Property="Height" Value="46" />
        <Setter Property="CornerRadius" Value="12" />
        <Setter Property="Background" Value="{DynamicResource DialogTonalBrush}" />
        <Setter Property="VerticalAlignment" Value="Center" />
    </Style>

    <Style x:Key="PanelModalTitle" TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource DialogTextBrush}" />
        <Setter Property="FontSize" Value="20" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
    </Style>

    <Style x:Key="PanelModalSubtitle" TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource DialogMutedBrush}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Margin" Value="0,4,0,0" />
        <Setter Property="TextWrapping" Value="Wrap" />
    </Style>

    <Style x:Key="PanelModalSectionTitle" TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource DialogTextBrush}" />
        <Setter Property="FontSize" Value="14" />
        <Setter Property="FontWeight" Value="SemiBold" />
    </Style>

    <Style x:Key="PanelModalBadge" TargetType="Border">
        <Setter Property="CornerRadius" Value="14" />
        <Setter Property="Padding" Value="11,5" />
        <Setter Property="Background" Value="{DynamicResource DialogTonalBrush}" />
        <Setter Property="VerticalAlignment" Value="Center" />
    </Style>
</ResourceDictionary>
'@

$controlsPath = 'Resources/PanelDialogControls.xaml'
$controls = Read-RepoText $controlsPath
if (-not $controls.Contains('PanelModalChrome.xaml')) {
    $controls = Replace-Once $controls `
        '        <ResourceDictionary Source="PanelScrollbars.xaml" />' `
        "        <ResourceDictionary Source=\"PanelModalChrome.xaml\" />`r`n        <ResourceDictionary Source=\"PanelScrollbars.xaml\" />" `
        'PanelModalChrome merge in dialog controls'
    Write-RepoText $controlsPath $controls
}

Write-Host '2/12 Rebuilding the standard message dialog...'
Write-RepoText 'PanelMessageDialog.xaml' @'
<local:PanelModalWindow x:Class="MCPanel.PanelMessageDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:MCPanel"
        PreviewKeyDown="Window_PreviewKeyDown">
    <local:PanelModalWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/PanelDialogControls.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </local:PanelModalWindow.Resources>

    <Border Style="{StaticResource PanelModalSurface}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="58" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Border Style="{StaticResource PanelModalIconBadge}">
                    <TextBlock x:Name="IconText"
                               FontFamily="Segoe MDL2 Assets"
                               FontSize="22"
                               HorizontalAlignment="Center"
                               VerticalAlignment="Center" />
                </Border>
                <StackPanel Grid.Column="1" Margin="12,1,0,0" VerticalAlignment="Center">
                    <TextBlock x:Name="TitleText" Style="{StaticResource PanelModalTitle}" />
                    <TextBlock x:Name="SubtitleText" Style="{StaticResource PanelModalSubtitle}" Text="操作提示" />
                </StackPanel>
            </Grid>

            <Border Grid.Row="1"
                    Style="{StaticResource PanelModalContentCard}"
                    Margin="0,22,0,22"
                    Padding="20,18">
                <ScrollViewer x:Name="MessageScrollViewer"
                              VerticalScrollBarVisibility="Auto"
                              HorizontalScrollBarVisibility="Disabled"
                              Focusable="True">
                    <ScrollViewer.Resources>
                        <Style TargetType="ScrollBar" BasedOn="{StaticResource ModernVerticalScrollBar}" />
                    </ScrollViewer.Resources>
                    <TextBlock x:Name="MessageText"
                               TextWrapping="Wrap"
                               FontSize="14"
                               LineHeight="23"
                               Foreground="{DynamicResource DialogTextBrush}"
                               VerticalAlignment="Top" />
                </ScrollViewer>
            </Border>

            <StackPanel x:Name="ButtonsPanel"
                        Grid.Row="2"
                        Orientation="Horizontal"
                        HorizontalAlignment="Right" />
        </Grid>
    </Border>
</local:PanelModalWindow>
'@

$panelMessageCodePath = 'PanelMessageDialog.xaml.cs'
$panelMessageCode = Read-RepoText $panelMessageCodePath
$panelMessageCode = Replace-Once $panelMessageCode 'public sealed partial class PanelMessageDialog : Window' 'public sealed partial class PanelMessageDialog : PanelModalWindow' 'PanelMessageDialog base class'
$oldTitle = '        TitleText.Text = string.IsNullOrWhiteSpace(caption) ? "MCPanel" : caption;'
$newTitle = @'
        TitleText.Text = string.IsNullOrWhiteSpace(caption) ? "MCPanel" : caption;
        SubtitleText.Text = image switch
        {
            MessageBoxImage.Warning => "请确认风险后继续",
            MessageBoxImage.Error => "操作未能完成",
            MessageBoxImage.Question => "请选择后续操作",
            _ => "操作提示"
        };
'@
$panelMessageCode = Replace-Once $panelMessageCode $oldTitle $newTitle.TrimEnd() 'message dialog subtitle initialization'
Write-RepoText $panelMessageCodePath $panelMessageCode

Write-Host '3/12 Rebuilding IIS website dialog...'
Write-RepoText 'CustomWebsiteDialog.xaml' @'
<local:PanelModalWindow x:Class="MCPanel.CustomWebsiteDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:MCPanel">
    <local:PanelModalWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/PanelDialogControls.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </local:PanelModalWindow.Resources>

    <Border Style="{StaticResource PanelModalSurface}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="58" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <Border Style="{StaticResource PanelModalIconBadge}">
                    <TextBlock Text="&#xE774;" FontFamily="Segoe MDL2 Assets" FontSize="21"
                               Foreground="{DynamicResource DialogPrimaryBrush}"
                               HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <StackPanel Grid.Column="1" Margin="12,1,16,0" VerticalAlignment="Center">
                    <TextBlock x:Name="TitleText" Text="新建 IIS 网站" Style="{StaticResource PanelModalTitle}" />
                    <TextBlock Text="网站、应用程序池、域名、SSL、HTTPS 跳转、MIME 与带宽统一配置。"
                               Style="{StaticResource PanelModalSubtitle}" />
                </StackPanel>
                <Border Grid.Column="2" Style="{StaticResource PanelModalBadge}">
                    <TextBlock Text="IIS" Foreground="{DynamicResource DialogTonalTextBrush}" FontWeight="SemiBold" FontSize="12" />
                </Border>
            </Grid>

            <Border Grid.Row="1" Style="{StaticResource PanelModalContentCard}" Margin="0,20,0,18" Padding="18,16">
                <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                    <StackPanel>
                        <Grid>
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="140"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,14,0">
                                <TextBlock Text="网站名称" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <TextBox x:Name="NameBox"/>
                            </StackPanel>
                            <StackPanel Grid.Column="1">
                                <TextBlock Text="HTTP 端口" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <TextBox x:Name="HttpPortBox"/>
                            </StackPanel>
                        </Grid>
                        <Grid Margin="0,13,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="86"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,10,0">
                                <TextBlock Text="网站根目录" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <TextBox x:Name="PhysicalPathBox"/>
                            </StackPanel>
                            <Button Grid.Column="1" Content="浏览" VerticalAlignment="Bottom" Margin="0" Click="BrowseRoot_Click"/>
                        </Grid>
                        <StackPanel Margin="0,13,0,0">
                            <TextBlock Text="域名（可留空；多个域名用逗号、空格或换行分隔）" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                            <TextBox x:Name="DomainsBox" Height="54" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"/>
                        </StackPanel>
                        <Grid Margin="0,13,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="250"/><ColumnDefinition Width="*"/><ColumnDefinition Width="190"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,14,0">
                                <TextBlock Text="应用程序池运行时" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <ComboBox x:Name="RuntimeBox" SelectedValuePath="Tag">
                                    <ComboBoxItem Content=".NET CLR v4.0" Tag="v4.0"/>
                                    <ComboBoxItem Content=".NET CLR v2.0/3.5" Tag="v2.0"/>
                                    <ComboBoxItem Content="无托管代码" Tag=""/>
                                </ComboBox>
                            </StackPanel>
                            <CheckBox Grid.Column="1" x:Name="Enable32BitBox" Content="启用 32 位应用" VerticalAlignment="Bottom" Margin="0,0,14,9"/>
                            <StackPanel Grid.Column="2">
                                <TextBlock Text="带宽上限 KB/s（0 不限）" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <TextBox x:Name="BandwidthBox"/>
                            </StackPanel>
                        </Grid>
                        <CheckBox x:Name="SslBox" Content="启用 HTTPS（使用 PFX/P12 证书）" Margin="0,17,0,0" Checked="SslOptionChanged" Unchecked="SslOptionChanged"/>
                        <Grid Margin="0,10,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="86"/><ColumnDefinition Width="130"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,10,0">
                                <TextBlock Text="PFX/P12 证书（编辑时可留空以复用已导入证书）" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <TextBox x:Name="PfxPathBox"/>
                            </StackPanel>
                            <Button Grid.Column="1" Content="浏览" VerticalAlignment="Bottom" Margin="0,0,10,0" Click="BrowsePfx_Click"/>
                            <StackPanel Grid.Column="2">
                                <TextBlock Text="HTTPS 端口" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <TextBox x:Name="HttpsPortBox"/>
                            </StackPanel>
                        </Grid>
                        <Grid Margin="0,10,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="280"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,16,0">
                                <TextBlock Text="PFX 密码" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                                <PasswordBox x:Name="PfxPasswordBox"/>
                            </StackPanel>
                            <CheckBox Grid.Column="1" x:Name="RedirectBox" Content="HTTP 永久跳转到 HTTPS" VerticalAlignment="Bottom" Margin="0,0,0,9"/>
                        </Grid>
                        <StackPanel Margin="0,13,0,0">
                            <TextBlock Text="自定义 MIME（每行：.扩展名=mime/type）" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,5"/>
                            <TextBox x:Name="MimeBox" Height="70" AcceptsReturn="True" TextWrapping="NoWrap" VerticalScrollBarVisibility="Auto"/>
                        </StackPanel>
                        <TextBlock x:Name="StatusText" Margin="0,13,0,0" Foreground="{DynamicResource DialogMutedBrush}" FontSize="12" TextWrapping="Wrap"
                                   Text="保存会触发一次管理员授权；业务文件不会被面板删除。"/>
                    </StackPanel>
                </ScrollViewer>
            </Border>

            <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
                <Button Content="取消" IsCancel="True" Click="Cancel_Click"/>
                <Button Content="保存并应用" IsDefault="True" Style="{StaticResource PrimaryDialogButton}" Click="Save_Click"/>
            </StackPanel>
        </Grid>
    </Border>
</local:PanelModalWindow>
'@

$customCodePath = 'CustomWebsiteDialog.xaml.cs'
$customCode = Read-RepoText $customCodePath
$customCode = Replace-Once $customCode 'public partial class CustomWebsiteDialog : Window' 'public partial class CustomWebsiteDialog : PanelModalWindow' 'CustomWebsiteDialog base class'
$customCode = [regex]::Replace($customCode, '(?m)^\s*SourceInitialized \+= \(_, _\) => ResponsiveWindowSizing\.FitToCurrentMonitor\(this, 780, 750, 0\.95, 1\.1\);\r?\n', '')
Write-RepoText $customCodePath $customCode

Write-Host '4/12 Rebuilding product domain and SSL dialog...'
Write-RepoText 'ProductWebsiteDialog.xaml' @'
<local:PanelModalWindow x:Class="MCPanel.ProductWebsiteDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:MCPanel">
    <local:PanelModalWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/PanelDialogControls.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </local:PanelModalWindow.Resources>

    <Border Style="{StaticResource PanelModalSurface}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="58" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <Border Style="{StaticResource PanelModalIconBadge}">
                    <TextBlock Text="&#xE774;" FontFamily="Segoe MDL2 Assets" FontSize="21"
                               Foreground="{DynamicResource DialogPrimaryBrush}"
                               HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <StackPanel Grid.Column="1" Margin="12,1,16,0" VerticalAlignment="Center">
                    <TextBlock Text="产品域名与 SSL" Style="{StaticResource PanelModalTitle}" />
                    <TextBlock Text="通过统一 Nginx 为当前产品配置独立域名、HTTPS、跳转和下载限速。"
                               Style="{StaticResource PanelModalSubtitle}" />
                </StackPanel>
                <Border Grid.Column="2" Style="{StaticResource PanelModalBadge}">
                    <TextBlock Text="Nginx Route" Foreground="{DynamicResource DialogTonalTextBrush}" FontWeight="SemiBold" FontSize="12" />
                </Border>
            </Grid>

            <Border Grid.Row="1" Style="{StaticResource PanelModalContentCard}" Margin="0,20,0,18" Padding="20,18">
                <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                    <StackPanel>
                        <CheckBox x:Name="EnabledBox" Content="启用独立域名访问" Checked="OptionChanged" Unchecked="OptionChanged" />
                        <Grid Margin="0,16,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="140"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,14,0">
                                <TextBlock Text="域名" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,6" />
                                <TextBox x:Name="DomainsBox" ToolTip="多个域名可用逗号或空格分隔" />
                            </StackPanel>
                            <StackPanel Grid.Column="1">
                                <TextBlock Text="HTTP 端口" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,6" />
                                <TextBox x:Name="HttpPortBox" />
                            </StackPanel>
                        </Grid>
                        <CheckBox x:Name="SslBox" Content="启用 HTTPS（PEM/CRT 证书 + PEM/KEY 私钥）"
                                  Margin="0,18,0,0" Checked="OptionChanged" Unchecked="OptionChanged" />
                        <Grid Margin="0,12,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="86"/><ColumnDefinition Width="120"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,10,0">
                                <TextBlock Text="证书文件" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,6" />
                                <TextBox x:Name="CertificateBox" />
                            </StackPanel>
                            <Button Grid.Column="1" Content="浏览" VerticalAlignment="Bottom" Margin="0,0,10,0" Click="BrowseCertificate_Click" />
                            <StackPanel Grid.Column="2">
                                <TextBlock Text="HTTPS 端口" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,6" />
                                <TextBox x:Name="HttpsPortBox" />
                            </StackPanel>
                        </Grid>
                        <Grid Margin="0,12,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="86"/></Grid.ColumnDefinitions>
                            <StackPanel Margin="0,0,10,0">
                                <TextBlock Text="私钥文件" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,6" />
                                <TextBox x:Name="CertificateKeyBox" />
                            </StackPanel>
                            <Button Grid.Column="1" Content="浏览" VerticalAlignment="Bottom" Margin="0" Click="BrowseKey_Click" />
                        </Grid>
                        <Grid Margin="0,18,0,0">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="190"/></Grid.ColumnDefinitions>
                            <CheckBox x:Name="RedirectBox" Content="HTTP 自动 301 跳转到 HTTPS" />
                            <StackPanel Grid.Column="1">
                                <TextBlock Text="单连接限速（KB/s，0 不限）" Foreground="{DynamicResource DialogMutedBrush}" Margin="0,0,0,6" />
                                <TextBox x:Name="BandwidthBox" />
                            </StackPanel>
                        </Grid>
                        <TextBlock x:Name="StatusText" Margin="0,18,0,0" TextWrapping="Wrap"
                                   Foreground="{DynamicResource DialogMutedBrush}" FontSize="12"
                                   Text="证书会复制到 Nginx 的独立配置目录；保存前会执行 nginx -t 校验，失败时自动恢复原配置。" />
                    </StackPanel>
                </ScrollViewer>
            </Border>

            <Grid Grid.Row="2">
                <Button x:Name="RemoveButton" Content="移除域名配置" HorizontalAlignment="Left" Margin="0" Click="Remove_Click" />
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                    <Button Content="取消" IsCancel="True" Click="Cancel_Click" />
                    <Button Content="保存并应用" IsDefault="True" Style="{StaticResource PrimaryDialogButton}" Click="Save_Click" />
                </StackPanel>
            </Grid>
        </Grid>
    </Border>
</local:PanelModalWindow>
'@

$productWebsiteCodePath = 'ProductWebsiteDialog.xaml.cs'
$productWebsiteCode = Read-RepoText $productWebsiteCodePath
$productWebsiteCode = Replace-Once $productWebsiteCode 'public partial class ProductWebsiteDialog : Window' 'public partial class ProductWebsiteDialog : PanelModalWindow' 'ProductWebsiteDialog base class'
$productWebsiteCode = [regex]::Replace($productWebsiteCode, '(?m)^\s*SourceInitialized \+= \(_, _\) => ResponsiveWindowSizing\.FitToCurrentMonitor\(this, 720, 650, 0\.94, 1\.15\);\r?\n', '')
Write-RepoText $productWebsiteCodePath $productWebsiteCode

Write-Host '5/12 Rebuilding Nginx management dialog...'
Write-RepoText 'NginxProxyDialog.xaml' @'
<local:PanelModalWindow x:Class="MCPanel.NginxProxyDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:MCPanel">
    <local:PanelModalWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/PanelDialogControls.xaml" />
            </ResourceDictionary.MergedDictionaries>
            <Style TargetType="DataGrid">
                <Setter Property="Background" Value="{DynamicResource SurfaceBrush}" />
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
                <Setter Property="BorderBrush" Value="{DynamicResource DialogLineBrush}" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="GridLinesVisibility" Value="None" />
                <Setter Property="HeadersVisibility" Value="Column" />
                <Setter Property="RowHeight" Value="38" />
                <Setter Property="ColumnHeaderHeight" Value="38" />
                <Setter Property="FontSize" Value="12" />
            </Style>
            <Style TargetType="DataGridColumnHeader">
                <Setter Property="Background" Value="{DynamicResource SurfaceAltBrush}" />
                <Setter Property="Foreground" Value="{DynamicResource DialogMutedBrush}" />
                <Setter Property="BorderBrush" Value="{DynamicResource DialogLineBrush}" />
                <Setter Property="Padding" Value="9,0" />
                <Setter Property="FontWeight" Value="SemiBold" />
            </Style>
            <Style TargetType="DataGridCell">
                <Setter Property="Foreground" Value="{DynamicResource DialogTextBrush}" />
                <Setter Property="BorderBrush" Value="Transparent" />
                <Setter Property="Padding" Value="7,0" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
            </Style>
        </ResourceDictionary>
    </local:PanelModalWindow.Resources>

    <Border Style="{StaticResource PanelModalSurface}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="58" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <Border Style="{StaticResource PanelModalIconBadge}">
                    <TextBlock Text="&#xE968;" FontFamily="Segoe MDL2 Assets" FontSize="21"
                               Foreground="{DynamicResource DialogPrimaryBrush}"
                               HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <StackPanel Grid.Column="1" Margin="12,1,16,0" VerticalAlignment="Center">
                    <TextBlock Text="Nginx 反向代理管理" Style="{StaticResource PanelModalTitle}" />
                    <TextBlock Text="批量管理监听端口、域名、路径、代理目标和 WebSocket 转发。"
                               Style="{StaticResource PanelModalSubtitle}" />
                </StackPanel>
                <Border Grid.Column="2" Style="{StaticResource PanelModalBadge}">
                    <TextBlock x:Name="RuleCountText" Foreground="{DynamicResource DialogTonalTextBrush}" FontWeight="SemiBold" FontSize="12" />
                </Border>
            </Grid>

            <Grid Grid.Row="1" Margin="0,18,0,12">
                <TextBlock Text="规则列表" Style="{StaticResource PanelModalSectionTitle}" VerticalAlignment="Center" />
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                    <Button Content="新增规则" Style="{StaticResource PrimaryDialogButton}" Click="AddRule_Click" />
                    <Button Content="复制选中" Style="{StaticResource DialogButton}" Margin="10,0,0,0" Click="DuplicateRule_Click" />
                    <Button Content="删除选中" Style="{StaticResource GhostDialogButton}" Margin="10,0,0,0" Click="DeleteRule_Click" />
                </StackPanel>
            </Grid>

            <Border Grid.Row="2" Style="{StaticResource PanelModalContentCard}" Padding="0">
                <DataGrid x:Name="RulesGrid"
                          AutoGenerateColumns="False"
                          CanUserAddRows="False"
                          CanUserDeleteRows="False"
                          SelectionMode="Single"
                          SelectionUnit="FullRow"
                          HorizontalScrollBarVisibility="Auto"
                          VerticalScrollBarVisibility="Auto">
                    <DataGrid.Columns>
                        <DataGridCheckBoxColumn Header="启用" Binding="{Binding Enabled}" Width="58" />
                        <DataGridTextColumn Header="名称" Binding="{Binding Name, UpdateSourceTrigger=PropertyChanged}" Width="120" />
                        <DataGridTextColumn Header="端口" Binding="{Binding ListenPort, UpdateSourceTrigger=PropertyChanged}" Width="76" />
                        <DataGridTextColumn Header="域名/主机" Binding="{Binding ServerName, UpdateSourceTrigger=PropertyChanged}" Width="120" />
                        <DataGridTextColumn Header="路径" Binding="{Binding LocationPath, UpdateSourceTrigger=PropertyChanged}" Width="82" />
                        <DataGridTextColumn Header="代理目标" Binding="{Binding ProxyTarget, UpdateSourceTrigger=PropertyChanged}" Width="*" MinWidth="180" />
                        <DataGridCheckBoxColumn Header="WebSocket" Binding="{Binding WebSocket}" Width="86" />
                    </DataGrid.Columns>
                </DataGrid>
            </Border>

            <Grid Grid.Row="3" Margin="0,16,0,0">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <TextBlock x:Name="StatusText" Foreground="{DynamicResource DialogMutedBrush}" FontSize="12" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                <StackPanel Grid.Column="1" Orientation="Horizontal">
                    <Button Content="取消" Style="{StaticResource DialogButton}" Margin="0,0,10,0" Click="Cancel_Click" />
                    <Button Content="保存并应用" Style="{StaticResource PrimaryDialogButton}" Click="Save_Click" />
                </StackPanel>
            </Grid>
        </Grid>
    </Border>
</local:PanelModalWindow>
'@

$nginxCodePath = 'NginxProxyDialog.xaml.cs'
$nginxCode = Read-RepoText $nginxCodePath
$nginxCode = Replace-Once $nginxCode 'public partial class NginxProxyDialog : Window' 'public partial class NginxProxyDialog : PanelModalWindow' 'NginxProxyDialog base class'
$nginxCode = [regex]::Replace($nginxCode, '(?m)^\s*SourceInitialized \+= \(_, _\) => ResponsiveWindowSizing\.FitToCurrentMonitor\(this, 980, 640, 0\.92, 1\.2\);\r?\n', '')
Write-RepoText $nginxCodePath $nginxCode

Write-Host '6/12 Adding unified MySQL input dialog...'
Write-RepoText 'PanelInputDialog.xaml' @'
<local:PanelModalWindow x:Class="MCPanel.PanelInputDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:MCPanel">
    <local:PanelModalWindow.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/PanelDialogControls.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </local:PanelModalWindow.Resources>

    <Border Style="{StaticResource PanelModalSurface}">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid>
                <Grid.ColumnDefinitions><ColumnDefinition Width="58"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <Border Style="{StaticResource PanelModalIconBadge}">
                    <TextBlock x:Name="IconText" Text="&#xE8D7;" FontFamily="Segoe MDL2 Assets" FontSize="21"
                               Foreground="{DynamicResource DialogPrimaryBrush}" HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <StackPanel Grid.Column="1" Margin="12,1,16,0" VerticalAlignment="Center">
                    <TextBlock x:Name="TitleText" Style="{StaticResource PanelModalTitle}" />
                    <TextBlock x:Name="SubtitleText" Style="{StaticResource PanelModalSubtitle}" />
                </StackPanel>
                <Border Grid.Column="2" Style="{StaticResource PanelModalBadge}">
                    <TextBlock Text="MySQL" Foreground="{DynamicResource DialogTonalTextBrush}" FontWeight="SemiBold" FontSize="12" />
                </Border>
            </Grid>

            <Border Grid.Row="1" Style="{StaticResource PanelModalContentCard}" Margin="0,22,0,22" Padding="24">
                <Grid VerticalAlignment="Center">
                    <StackPanel x:Name="TextPanel" Width="520" HorizontalAlignment="Center">
                        <TextBlock x:Name="InputLabel" Text="端口" Foreground="{DynamicResource DialogMutedBrush}" FontSize="13" Margin="0,0,0,7" />
                        <TextBox x:Name="TextInput" FontSize="15" />
                        <TextBlock Text="修改后会同步更新 MySQL 配置与面板连接信息。" Foreground="{DynamicResource DialogMutedBrush}" FontSize="12" Margin="0,10,0,0" />
                    </StackPanel>
                    <StackPanel x:Name="PasswordPanel" Width="520" HorizontalAlignment="Center" Visibility="Collapsed">
                        <TextBlock Text="新密码" Foreground="{DynamicResource DialogMutedBrush}" FontSize="13" Margin="0,0,0,7" />
                        <PasswordBox x:Name="PasswordInput" FontSize="15" />
                        <TextBlock Text="再次输入" Foreground="{DynamicResource DialogMutedBrush}" FontSize="13" Margin="0,14,0,7" />
                        <PasswordBox x:Name="PasswordConfirmation" FontSize="15" />
                        <TextBlock Text="1–64 个字符；允许纯数字，不允许换行或制表符。" Foreground="{DynamicResource DialogMutedBrush}" FontSize="12" Margin="0,10,0,0" />
                    </StackPanel>
                    <TextBlock x:Name="StatusText" VerticalAlignment="Bottom" HorizontalAlignment="Center" TextWrapping="Wrap"
                               Foreground="{DynamicResource DialogDangerBrush}" FontSize="12" Margin="0,16,0,0" />
                </Grid>
            </Border>

            <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
                <Button Content="取消" IsCancel="True" Click="Cancel_Click" />
                <Button x:Name="ConfirmButton" Content="确认修改" IsDefault="True" Style="{StaticResource PrimaryDialogButton}" Click="Confirm_Click" />
            </StackPanel>
        </Grid>
    </Border>
</local:PanelModalWindow>
'@

Write-RepoText 'PanelInputDialog.xaml.cs' @'
using System.Globalization;
using System.Windows;

namespace MCPanel;

internal enum PanelInputDialogMode
{
    Port,
    Password
}

public partial class PanelInputDialog : PanelModalWindow
{
    private readonly PanelInputDialogMode _mode;

    private PanelInputDialog(PanelInputDialogMode mode)
    {
        InitializeComponent();
        _mode = mode;
        Loaded += (_, _) =>
        {
            if (_mode == PanelInputDialogMode.Password)
            {
                PasswordInput.Focus();
            }
            else
            {
                TextInput.Focus();
                TextInput.SelectAll();
            }
        };
    }

    public string? ResultText { get; private set; }

    public static PanelInputDialog CreatePortEditor(int currentPort)
    {
        var dialog = new PanelInputDialog(PanelInputDialogMode.Port);
        dialog.TitleText.Text = "修改 MySQL 端口";
        dialog.SubtitleText.Text = "更新 MySQL 服务监听端口，并同步面板连接配置。";
        dialog.IconText.Text = "\uE8D7";
        dialog.InputLabel.Text = "MySQL 端口";
        dialog.TextInput.Text = currentPort.ToString(CultureInfo.InvariantCulture);
        dialog.TextPanel.Visibility = Visibility.Visible;
        dialog.PasswordPanel.Visibility = Visibility.Collapsed;
        return dialog;
    }

    public static PanelInputDialog CreatePasswordEditor()
    {
        var dialog = new PanelInputDialog(PanelInputDialogMode.Password);
        dialog.TitleText.Text = "修改 MySQL 密码";
        dialog.SubtitleText.Text = "更新 root 密码，并同步 MCPanel 保存的连接凭据。";
        dialog.IconText.Text = "\uE72E";
        dialog.TextPanel.Visibility = Visibility.Collapsed;
        dialog.PasswordPanel.Visibility = Visibility.Visible;
        return dialog;
    }

    public void ApplyTheme(bool dark) => PanelThemeService.Apply(dark, Resources);

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        if (_mode == PanelInputDialogMode.Port)
        {
            var text = TextInput.Text.Trim();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                StatusText.Text = "请输入 1 到 65535 之间的有效端口号。";
                TextInput.Focus();
                TextInput.SelectAll();
                return;
            }

            ResultText = text;
        }
        else
        {
            var password = PasswordInput.Password;
            if (password.Length is < 1 or > 64)
            {
                StatusText.Text = "密码不能为空，最多 64 个字符；纯数字密码也可以。";
                PasswordInput.Focus();
                PasswordInput.SelectAll();
                return;
            }

            if (password.Any(char.IsControl))
            {
                StatusText.Text = "密码不能包含换行、制表符等控制字符。";
                PasswordInput.Focus();
                PasswordInput.SelectAll();
                return;
            }

            if (!string.Equals(password, PasswordConfirmation.Password, StringComparison.Ordinal))
            {
                StatusText.Text = "两次输入的密码不一致。";
                PasswordConfirmation.Focus();
                PasswordConfirmation.SelectAll();
                return;
            }

            ResultText = password;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
'@

Write-Host '7/12 Replacing legacy programmatic MySQL windows...'
$environmentPath = 'MainWindow.Environment.cs'
$environmentCode = Read-RepoText $environmentPath
$editorPattern = '(?ms)    private string\? ShowPortEditor\(int currentPort\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    private string\? ShowPasswordEditor\(\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    internal void CredentialConnect_Click'
$editorReplacement = @'
    private string? ShowPortEditor(int currentPort)
    {
        var dialog = PanelInputDialog.CreatePortEditor(currentPort)
        {
            Owner = this
        };
        dialog.ApplyTheme(_isDarkThemeActive);
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    private string? ShowPasswordEditor()
    {
        var dialog = PanelInputDialog.CreatePasswordEditor()
        {
            Owner = this
        };
        dialog.ApplyTheme(_isDarkThemeActive);
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    internal void CredentialConnect_Click
'@
$environmentCode = Replace-RegexOnce $environmentCode $editorPattern $editorReplacement.TrimEnd("`r", "`n") 'MySQL programmatic editor windows'
Write-RepoText $environmentPath $environmentCode

Write-Host '8/12 Unifying installation/deployment overlay card...'
$mainXamlPath = 'MainWindow.xaml'
$mainXaml = Read-RepoText $mainXamlPath
if (-not $mainXaml.Contains('Resources/PanelModalChrome.xaml')) {
    $mainXaml = Replace-Once $mainXaml `
        '                <ResourceDictionary Source="Resources/PanelTheme.xaml" />' `
        "                <ResourceDictionary Source=\"Resources/PanelTheme.xaml\" />`r`n                <ResourceDictionary Source=\"Resources/PanelModalChrome.xaml\" />" `
        'PanelModalChrome merge in MainWindow'
}
$overlayMarker = 'Visibility="{Binding InstallationProgress.IsVisible, Converter={StaticResource BooleanToVisibility}}"'
$overlayIndex = $mainXaml.IndexOf($overlayMarker, [System.StringComparison]::Ordinal)
if ($overlayIndex -lt 0) { throw 'Unable to locate installation overlay.' }
$borderIndex = $mainXaml.IndexOf('<Border', $overlayIndex, [System.StringComparison]::Ordinal)
$borderEnd = $mainXaml.IndexOf('>', $borderIndex)
if ($borderIndex -lt 0 -or $borderEnd -lt 0) { throw 'Unable to locate installation overlay card opening tag.' }
$opening = $mainXaml.Substring($borderIndex, $borderEnd - $borderIndex + 1)
$opening = [regex]::Replace($opening, '\s+(Width|Height|MaxHeight|Background|BorderBrush|BorderThickness|CornerRadius|Padding)="[^"]*"', '')
if (-not $opening.Contains('Style="{StaticResource PanelModalSurface}"')) {
    $opening = $opening.Replace('<Border', '<Border Style="{StaticResource PanelModalSurface}"')
}
$mainXaml = $mainXaml.Substring(0, $borderIndex) + $opening + $mainXaml.Substring($borderEnd + 1)
Write-RepoText $mainXamlPath $mainXaml

Write-Host '9/12 Bumping MCPanel to 1.3.15...'
$projectPath = 'MCPanel.csproj'
$project = Read-RepoText $projectPath
$project = Replace-Once $project '<Version>1.3.14</Version>' '<Version>1.3.15</Version>' 'application version'
$project = Replace-Once $project '<FileVersion>1.3.14.0</FileVersion>' '<FileVersion>1.3.15.0</FileVersion>' 'file version'
$project = Replace-Once $project '<AssemblyVersion>1.3.14.0</AssemblyVersion>' '<AssemblyVersion>1.3.15.0</AssemblyVersion>' 'assembly version'
Write-RepoText $projectPath $project

Write-Host '10/12 Adding modal UI regression coverage...'
Write-RepoText 'MCPanel.Tests/ReliabilityTests.DialogUi115.cs' @'
using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void DialogUi115_AllOwnedBusinessDialogsUseOneModalChromeAndScrim()
    {
        var modal = ReadRepositoryFile("PanelModalWindow.cs");
        var chrome = ReadRepositoryFile(Path.Combine("Resources", "PanelModalChrome.xaml"));
        var main = ReadRepositoryFile("MainWindow.xaml");
        var environment = ReadRepositoryFile("MainWindow.Environment.cs");

        StringAssert.Contains(modal, "StandardCardWidth = 820d");
        StringAssert.Contains(modal, "StandardCardHeight = 600d");
        StringAssert.Contains(modal, "Color.FromArgb(0x78, 0x00, 0x00, 0x00)");
        StringAssert.Contains(chrome, "x:Key=\"PanelModalSurface\"");
        StringAssert.Contains(chrome, "<Setter Property=\"Width\" Value=\"820\" />");
        StringAssert.Contains(chrome, "<Setter Property=\"Height\" Value=\"600\" />");

        foreach (var path in new[]
                 {
                     "PanelMessageDialog.xaml",
                     "CustomWebsiteDialog.xaml",
                     "ProductWebsiteDialog.xaml",
                     "NginxProxyDialog.xaml",
                     "PanelInputDialog.xaml"
                 })
        {
            var xaml = ReadRepositoryFile(path);
            StringAssert.Contains(xaml, "<local:PanelModalWindow");
            StringAssert.Contains(xaml, "Style=\"{StaticResource PanelModalSurface}\"");
        }

        StringAssert.Contains(main, "<Popup x:Name=\"DownloadQueuePopup\"");
        StringAssert.Contains(main, "Background=\"#78000000\"");
        StringAssert.Contains(main, "Visibility=\"{Binding InstallationProgress.IsVisible, Converter={StaticResource BooleanToVisibility}}\"");
        StringAssert.Contains(main, "Style=\"{StaticResource PanelModalSurface}\"");

        StringAssert.Contains(environment, "PanelInputDialog.CreatePortEditor(currentPort)");
        StringAssert.Contains(environment, "PanelInputDialog.CreatePasswordEditor()");
        Assert.IsFalse(environment.Contains("new Window", StringComparison.Ordinal),
            "Legacy ad-hoc MySQL windows must not bypass the unified modal system.");
    }

    [TestMethod]
    public void DialogUi115_NoLegacyXamlWindowShellsRemainOutsideMainWindow()
    {
        var root = FindRepositoryRoot115();
        var offenders = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("<Window x:Class=", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.AreEqual(0, offenders.Length,
            "All application-owned XAML dialogs must derive from PanelModalWindow. Offenders: " + string.Join(", ", offenders));
    }

    private static string FindRepositoryRoot115()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MCPanel.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        Assert.Fail("Unable to locate MCPanel repository root.");
        return string.Empty;
    }
}
'@

Write-Host '11/12 Updating release workflow notes for the unified modal system...'
$releasePath = '.github/workflows/release.yml'
$release = Read-RepoText $releasePath
$release = [regex]::Replace(
    $release,
    "-ReleaseNotes '[^']*'",
    "-ReleaseNotes '统一 MCPanel 业务弹窗为 820x600 模态卡片：自定义消息框、IIS 网站、产品域名与 SSL、Nginx 管理、MySQL 端口/密码与安装部署进度均使用同一圆角外壳和黑色聚焦遮罩；下载队列悬浮 Popup 保持不变。'",
    1)
Write-RepoText $releasePath $release

Write-Host '12/12 Verifying migration invariants...'
foreach ($required in @(
    'PanelModalWindow.cs',
    'Resources/PanelModalChrome.xaml',
    'PanelInputDialog.xaml',
    'PanelInputDialog.xaml.cs',
    'MCPanel.Tests/ReliabilityTests.DialogUi115.cs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $required))) {
        throw "Missing expected migration output: $required"
    }
}

$environmentAfter = Read-RepoText 'MainWindow.Environment.cs'
if ($environmentAfter.Contains('new Window')) {
    throw 'Legacy ad-hoc Window creation still exists in MainWindow.Environment.cs.'
}

Write-Host 'Modal UI migration 1.3.15 applied successfully.'
