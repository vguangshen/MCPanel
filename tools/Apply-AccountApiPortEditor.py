from pathlib import Path
from textwrap import dedent


def read(path):
    return Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one occurrence, found {count}: {old!r}")
    write(path, text.replace(old, new, 1))


# 1) Service: persist a new port and restart transactionally when enabled.
service = read("AccountApiManagerService.cs")
service_marker = "    public string GenerateSigningSecret()\n"
if service.count(service_marker) != 1:
    raise RuntimeError("GenerateSigningSecret insertion marker not found exactly once.")
method = dedent(
    '''
    public async Task SetPortAsync(int port, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Account API 监听端口必须在 1 到 65535 之间。");
        }

        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            _enabled = LoadSettings().Enabled;
            var configuration = RequireConfiguration();
            var previousPort = configuration.Port;
            if (port == previousPort)
            {
                return;
            }

            var wasRunning = EmbeddedAccountApiRuntime.IsRunning;
            if (wasRunning)
            {
                EmbeddedAccountApiRuntime.Stop();
            }

            try
            {
                SetIniValue(
                    configuration.ConfigPath,
                    "AccountApi:Server",
                    "Port",
                    port.ToString(System.Globalization.CultureInfo.InvariantCulture));

                if (_enabled)
                {
                    var updatedConfiguration = LoadConfiguration();
                    if (!updatedConfiguration.HasSigningSecret)
                    {
                        GenerateSigningSecret(updatedConfiguration);
                    }

                    updatedConfiguration = RequireRunnableConfiguration();
                    EmbeddedAccountApiRuntime.Start(updatedConfiguration.ConfigPath);
                    if (!EmbeddedAccountApiRuntime.IsRunning)
                    {
                        throw new InvalidOperationException(
                            $"内置 Account API 修改到端口 {port} 后未进入监听状态。");
                    }
                }
            }
            catch (Exception applyError)
            {
                EmbeddedAccountApiRuntime.Stop();
                Exception? restoreError = null;
                try
                {
                    SetIniValue(
                        configuration.ConfigPath,
                        "AccountApi:Server",
                        "Port",
                        previousPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (wasRunning)
                    {
                        EmbeddedAccountApiRuntime.Start(configuration.ConfigPath);
                    }
                }
                catch (Exception error)
                {
                    restoreError = error;
                }

                if (restoreError is not null)
                {
                    throw new InvalidOperationException(
                        $"监听端口 {port} 应用失败，并且恢复原端口 {previousPort} 时也失败。请检查 Account API 日志。",
                        new AggregateException(applyError, restoreError));
                }

                if (applyError is HttpListenerException)
                {
                    throw new InvalidOperationException(
                        $"内置 Account API 无法监听端口 {port}。该端口可能已被其他软件占用，已恢复原端口 {previousPort}。",
                        applyError);
                }

                throw new InvalidOperationException(
                    $"修改 Account API 监听端口失败，已恢复原端口 {previousPort}。",
                    applyError);
            }
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    '''
)
method = "".join("    " + line if line.strip() else line for line in method.splitlines(True))
service = service.replace(service_marker, method + service_marker, 1)
write("AccountApiManagerService.cs", service)


# 2) Unified input dialog: make badge/hint contextual and add Account API factory.
replace_once(
    "PanelInputDialog.xaml",
    '<TextBlock Text="MySQL" Foreground="{DynamicResource DialogTonalTextBrush}" FontWeight="SemiBold" FontSize="12" />',
    '<TextBlock x:Name="ContextBadgeText" Text="MySQL" Foreground="{DynamicResource DialogTonalTextBrush}" FontWeight="SemiBold" FontSize="12" />',
)
replace_once(
    "PanelInputDialog.xaml",
    '<TextBlock Text="修改后会同步更新 MySQL 配置与面板连接信息。" Foreground="{DynamicResource DialogMutedBrush}" FontSize="12" Margin="0,10,0,0" />',
    '<TextBlock x:Name="TextHint" Text="修改后会同步更新 MySQL 配置与面板连接信息。" Foreground="{DynamicResource DialogMutedBrush}" FontSize="12" Margin="0,10,0,0" />',
)

dialog = read("PanelInputDialog.xaml.cs")
start = dialog.index("    public static PanelInputDialog CreatePortEditor(int currentPort)")
end = dialog.index("    public static PanelInputDialog CreatePasswordEditor()", start)
port_block = dialog[start:end]
icon_marker = '        dialog.IconText.Text = "\\uE8D7";\n'
if port_block.count(icon_marker) != 1:
    raise RuntimeError("MySQL port editor icon marker missing.")
port_block = port_block.replace(
    icon_marker,
    icon_marker + '        dialog.ContextBadgeText.Text = "MySQL";\n',
    1,
)
input_marker = "        dialog.TextInput.Text = currentPort.ToString(CultureInfo.InvariantCulture);\n"
if port_block.count(input_marker) != 1:
    raise RuntimeError("MySQL port editor input marker missing.")
port_block = port_block.replace(
    input_marker,
    input_marker + '        dialog.TextHint.Text = "修改后会同步更新 MySQL 配置与面板连接信息。";\n',
    1,
)
account_factory = dedent(
    '''
    public static PanelInputDialog CreateAccountApiPortEditor(int currentPort)
    {
        var dialog = new PanelInputDialog(PanelInputDialogMode.Port);
        dialog.TitleText.Text = "修改 Account API 监听端口";
        dialog.SubtitleText.Text = "修改内置 Account API 的监听端口，用于避开其他软件的端口占用。";
        dialog.IconText.Text = "\\uE8D7";
        dialog.ContextBadgeText.Text = "Account API";
        dialog.InputLabel.Text = "监听端口";
        dialog.TextInput.Text = currentPort.ToString(CultureInfo.InvariantCulture);
        dialog.TextHint.Text = "端口范围 1–65535；API 已启用时确认后会立即切换，失败会自动恢复原端口。";
        dialog.TextPanel.Visibility = Visibility.Visible;
        dialog.PasswordPanel.Visibility = Visibility.Collapsed;
        return dialog;
    }

    '''
)
account_factory = "".join("    " + line if line.strip() else line for line in account_factory.splitlines(True))
dialog = dialog[:start] + port_block + account_factory + dialog[end:]
write("PanelInputDialog.xaml.cs", dialog)


# 3) Listening-address card: add compact edit action without changing the address display.
xaml = read("AccountApiPage.xaml")
card_start = xaml.index('            <Border Grid.Column="1" Style="{DynamicResource Card}" Padding="12,8" Margin="0,0,6,0">')
card_end = xaml.index('            <Border Grid.Column="2" Style="{DynamicResource Card}"', card_start)
replacement = '''            <Border Grid.Column="1" Style="{DynamicResource Card}" Padding="12,8" Margin="0,0,6,0">
                <StackPanel>
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="监听地址" Style="{StaticResource AccountCaption}" VerticalAlignment="Center" />
                        <Button Grid.Column="1"
                                Content="编辑"
                                Style="{StaticResource AccountLinkButton}"
                                Height="22"
                                Padding="7,0"
                                Margin="6,-3,0,-3"
                                Click="EditEndpoint_Click"
                                IsEnabled="{Binding CanEditEndpoint}"
                                ToolTip="修改 Account API 监听端口"
                                AutomationProperties.Name="修改 Account API 监听端口" />
                    </Grid>
                    <TextBlock Text="{Binding EndpointText}" Style="{StaticResource AccountMetricValue}" Margin="0,3,0,0" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding AuthModeText}" FontSize="11" Foreground="{DynamicResource MutedBrush}" Margin="0,2,0,0" TextTrimming="CharacterEllipsis" />
                </StackPanel>
            </Border>
'''
xaml = xaml[:card_start] + replacement + xaml[card_end:]
write("AccountApiPage.xaml", xaml)


# 4) Page behavior and binding state.
replace_once(
    "AccountApiPage.xaml.cs",
    "    public bool CanOpenConfig => _snapshot?.Configuration.ConfigExists == true;\n",
    "    public bool CanOpenConfig => _snapshot?.Configuration.ConfigExists == true;\n    public bool CanEditEndpoint => !IsBusy && _snapshot?.Configuration.ConfigExists == true;\n",
)
replace_once(
    "AccountApiPage.xaml.cs",
    '            "停用账号 API" => succeeded ? "账号 API 已停用" : "账号 API 停用失败",\n',
    '            "停用账号 API" => succeeded ? "账号 API 已停用" : "账号 API 停用失败",\n            "修改监听端口" => succeeded ? "监听端口已更新" : "监听端口修改失败",\n',
)
page = read("AccountApiPage.xaml.cs")
enabled_marker = "    private async void Enabled_Click(object sender, RoutedEventArgs e)\n"
if page.count(enabled_marker) != 1:
    raise RuntimeError("Enabled_Click marker missing.")
handler = dedent(
    '''
    private async void EditEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _snapshot?.Configuration.ConfigExists != true)
        {
            return;
        }

        var currentPort = _snapshot.Configuration.Port;
        var dialog = PanelInputDialog.CreateAccountApiPortEditor(currentPort);
        var owner = Window.GetWindow(this);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() != true ||
            !int.TryParse(dialog.ResultText, out var port) ||
            port == currentPort)
        {
            return;
        }

        await RunOperationAsync(
            "正在修改监听端口…",
            () => _service.SetPortAsync(port));
    }

    '''
)
handler = "".join("    " + line if line.strip() else line for line in handler.splitlines(True))
page = page.replace(enabled_marker, handler + enabled_marker, 1)
write("AccountApiPage.xaml.cs", page)


# 5) Regression/source-contract coverage.
test_path = Path("MCPanel.Tests/ReliabilityTests.AccountApiPortEditing.cs")
if test_path.exists():
    raise RuntimeError(f"{test_path} already exists")
test_path.write_text(
    dedent(
        r'''
        using System;
        using System.IO;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace MCPanel.Tests;

        public sealed partial class ReliabilityTests
        {
            [TestMethod]
            public void AccountApiPortEditorIsWiredToTransactionalRuntimeRestart()
            {
                var service = ReadAccountApiPortEditingSource("AccountApiManagerService.cs");
                var pageXaml = ReadAccountApiPortEditingSource("AccountApiPage.xaml");
                var pageCode = ReadAccountApiPortEditingSource("AccountApiPage.xaml.cs");
                var dialogXaml = ReadAccountApiPortEditingSource("PanelInputDialog.xaml");
                var dialogCode = ReadAccountApiPortEditingSource("PanelInputDialog.xaml.cs");

                StringAssert.Contains(service, "public async Task SetPortAsync(int port");
                StringAssert.Contains(service, "port is < 1 or > 65535");
                StringAssert.Contains(service, "\"AccountApi:Server\"");
                StringAssert.Contains(service, "var previousPort = configuration.Port;");
                StringAssert.Contains(service, "var wasRunning = EmbeddedAccountApiRuntime.IsRunning;");
                StringAssert.Contains(service, "EmbeddedAccountApiRuntime.Start(updatedConfiguration.ConfigPath);");
                StringAssert.Contains(service, "previousPort.ToString(System.Globalization.CultureInfo.InvariantCulture)");
                StringAssert.Contains(service, "已恢复原端口");

                StringAssert.Contains(pageXaml, "Content=\"编辑\"");
                StringAssert.Contains(pageXaml, "Click=\"EditEndpoint_Click\"");
                StringAssert.Contains(pageXaml, "IsEnabled=\"{Binding CanEditEndpoint}\"");
                StringAssert.Contains(pageCode, "PanelInputDialog.CreateAccountApiPortEditor(currentPort)");
                StringAssert.Contains(pageCode, "_service.SetPortAsync(port)");
                StringAssert.Contains(pageCode, "public bool CanEditEndpoint");

                StringAssert.Contains(dialogXaml, "x:Name=\"ContextBadgeText\"");
                StringAssert.Contains(dialogXaml, "x:Name=\"TextHint\"");
                StringAssert.Contains(dialogCode, "CreateAccountApiPortEditor(int currentPort)");
                StringAssert.Contains(dialogCode, "端口范围 1–65535");
                StringAssert.Contains(dialogCode, "port is < 1 or > 65535");
            }

            private static string ReadAccountApiPortEditingSource(string relativePath)
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null)
                {
                    var candidate = Path.Combine(directory.FullName, relativePath);
                    if (File.Exists(Path.Combine(directory.FullName, "MCPanel.csproj")) && File.Exists(candidate))
                    {
                        return File.ReadAllText(candidate);
                    }

                    directory = directory.Parent;
                }

                Assert.Fail("Unable to locate repository source: " + relativePath);
                return string.Empty;
            }
        }
        '''
    ),
    encoding="utf-8",
    newline="\n",
)
