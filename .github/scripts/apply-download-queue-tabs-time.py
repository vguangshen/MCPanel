from pathlib import Path
from textwrap import dedent, indent


# Reuse the reviewed split-view patch plus its updated UI regressions.
workflow = Path('.github/workflows/one-time-download-queue-tabs-retry.yml').read_text(encoding='utf-8-sig').splitlines()
start = next(i for i, line in enumerate(workflow) if line.strip() == "@'")
end = next(i for i, line in enumerate(workflow[start + 1:], start + 1) if line.strip() == "'@ | python -")
lines = [line[10:] if line.startswith('          ') else line for line in workflow[start + 1:end]]
script = '\n'.join(lines) + '\n'
exec(compile(script, 'download-queue-tabs-time-base.py', 'exec'))


def read(path):
    return Path(path).read_text(encoding='utf-8-sig').replace('\r\n', '\n')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError(label + ' anchor not found')
    return text.replace(old, new, 1)


# Durable completion timestamp: stamp terminal transitions, persist it,
# and restore it. Older history rows fall back to the progress file mtime.
queue_path = 'ProductInstallQueue.cs'
queue = read(queue_path)

if '    private DateTime? _completedAtUtc;\n' not in queue:
    queue = replace_once(
        queue,
        '    private int _scannedFiles;\n',
        '    private int _scannedFiles;\n    private DateTime? _completedAtUtc;\n',
        'completion timestamp field')

old_ctor = '''    internal ProductInstallQueueItemViewModel(
        string queueId,
        int sequence,
        ProductInstallWorkerRequest request,
        ProductInstallQueueStatus state,
        double progress,
        string message)
'''
new_ctor = '''    internal ProductInstallQueueItemViewModel(
        string queueId,
        int sequence,
        ProductInstallWorkerRequest request,
        ProductInstallQueueStatus state,
        double progress,
        string message,
        DateTime? completedAtUtc = null)
'''
if '        DateTime? completedAtUtc = null)\n' not in queue:
    queue = replace_once(queue, old_ctor, new_ctor, 'queue item constructor')
    queue = replace_once(
        queue,
        '        _message = message;\n    }\n',
        '        _message = message;\n        _completedAtUtc = ProductInstallQueueService.IsTerminal(state) ? completedAtUtc : null;\n    }\n',
        'queue item constructor assignment')

if '    public DateTime? CompletedAtUtc => _completedAtUtc;\n' not in queue:
    message_prop = '    public string Message { get => _message; private set => SetProperty(ref _message, value); }\n'
    completion_props = '''    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public DateTime? CompletedAtUtc => _completedAtUtc;
    public string CompletionTimeText
    {
        get
        {
            if (!IsTerminal)
            {
                return string.Empty;
            }

            var label = State == ProductInstallQueueStatus.Completed ? "部署完成" : "任务结束";
            return CompletedAtUtc is DateTime completedAtUtc
                ? $"{label} · {completedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                : $"{label}时间未记录";
        }
    }
'''
    queue = replace_once(queue, message_prop, completion_props, 'completion display property')

old_set_state = '''    internal void SetState(ProductInstallQueueStatus state)
    {
        State = state;
        if (ProductInstallQueueService.IsTerminal(state))
        {
            SetPaused(false);
        }
    }
'''
new_set_state = '''    internal void SetState(ProductInstallQueueStatus state)
    {
        State = state;
        if (ProductInstallQueueService.IsTerminal(state))
        {
            if (_completedAtUtc is null)
            {
                _completedAtUtc = DateTime.UtcNow;
                OnPropertyChanged(nameof(CompletedAtUtc));
                OnPropertyChanged(nameof(CompletionTimeText));
            }

            SetPaused(false);
        }
    }
'''
if '            _completedAtUtc = DateTime.UtcNow;\n' not in queue:
    queue = replace_once(queue, old_set_state, new_set_state, 'terminal timestamp transition')

old_restore = '''            var item = new ProductInstallQueueItemViewModel(
                persisted.QueueId,
                persisted.Sequence,
                persisted.Request,
                state,
                restoredProgress,
                message);
'''
new_restore = '''            var item = new ProductInstallQueueItemViewModel(
                persisted.QueueId,
                persisted.Sequence,
                persisted.Request,
                state,
                restoredProgress,
                message,
                isTerminal
                    ? persisted.CompletedAtUtc ?? TryGetCompletionTimeUtc(persisted.ProgressPath)
                    : null);
'''
if 'persisted.CompletedAtUtc ?? TryGetCompletionTimeUtc' not in queue:
    queue = replace_once(queue, old_restore, new_restore, 'restore completion timestamp')

if '    private static DateTime? TryGetCompletionTimeUtc(string? progressPath)\n' not in queue:
    helper_marker = '    private void StopPreviousSession(string? previousSessionId)\n'
    helper = '''    private static DateTime? TryGetCompletionTimeUtc(string? progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath))
        {
            return null;
        }

        try
        {
            return File.Exists(progressPath) ? File.GetLastWriteTimeUtc(progressPath) : null;
        }
        catch
        {
            return null;
        }
    }

'''
    queue = replace_once(queue, helper_marker, helper + helper_marker, 'legacy completion timestamp helper')

old_persist_initializer = '''                    {
                        IsRemovalRequested = item.IsRemovalRequested
                    })
'''
new_persist_initializer = '''                    {
                        IsRemovalRequested = item.IsRemovalRequested,
                        CompletedAtUtc = item.CompletedAtUtc
                    })
'''
if '                        CompletedAtUtc = item.CompletedAtUtc\n' not in queue:
    queue = replace_once(queue, old_persist_initializer, new_persist_initializer, 'persist completion timestamp')

old_record_tail = '''{
    public bool IsRemovalRequested { get; init; }
}
'''
new_record_tail = '''{
    public bool IsRemovalRequested { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
}
'''
if '    public DateTime? CompletedAtUtc { get; init; }\n' not in queue:
    queue = replace_once(queue, old_record_tail, new_record_tail, 'persisted completion timestamp field')

write(queue_path, queue)


# History row: put the terminal time in the lower-right corner without
# competing with the product name, state badge, or delete action.
templates_path = 'Resources/MainWindowTemplates.xaml'
templates = read(templates_path)
history_start = templates.index('    <DataTemplate x:Key="InstallationQueueHistoryItemTemplate">')
history_end = templates.index('    <DataTemplate x:Key="InstallationRecentEventTemplate">', history_start)
history = indent(dedent('''\
<DataTemplate x:Key="InstallationQueueHistoryItemTemplate">
    <Border Padding="8,9,8,8" Background="Transparent" BorderBrush="{DynamicResource LineBrush}" BorderThickness="0,0,0,1">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="52" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="76" />
                <ColumnDefinition Width="40" />
            </Grid.ColumnDefinitions>
            <Border Grid.RowSpan="2" Width="42" Height="42" HorizontalAlignment="Center" VerticalAlignment="Center"
                    CornerRadius="8" Background="{DynamicResource SurfaceAltBrush}"
                    BorderBrush="{DynamicResource CardLineBrush}" BorderThickness="1" ToolTip="{Binding ProductName}">
                <Image Source="{Binding IconPath}" Stretch="Uniform" Margin="3" RenderOptions.BitmapScalingMode="HighQuality" />
            </Border>
            <StackPanel Grid.Column="1" Margin="10,0,10,0" VerticalAlignment="Center">
                <TextBlock Text="{Binding ProductName}" ToolTip="{Binding ProductName}"
                           Foreground="{DynamicResource TextBrush}" FontSize="12" FontWeight="SemiBold"
                           TextTrimming="CharacterEllipsis" />
                <TextBlock FontSize="10" Foreground="{DynamicResource MutedBrush}" Margin="0,3,0,0" TextTrimming="CharacterEllipsis">
                    <Run Text="{Binding ProductId, Mode=OneWay}" />
                    <Run Text=" · " />
                    <Run Text="{Binding Message, Mode=OneWay}" />
                </TextBlock>
            </StackPanel>
            <Border Grid.Column="2" Height="26" MinWidth="64" Padding="8,0" CornerRadius="13"
                    Background="{DynamicResource SurfaceAltBrush}" BorderBrush="{DynamicResource CardLineBrush}"
                    BorderThickness="1" HorizontalAlignment="Right" VerticalAlignment="Top">
                <TextBlock Text="{Binding StateText}" FontSize="10.5" FontWeight="SemiBold"
                           HorizontalAlignment="Center" VerticalAlignment="Center">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Foreground" Value="{DynamicResource MutedBrush}" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding ProgressText}" Value="完成">
                                    <Setter Property="Foreground" Value="{DynamicResource MemoryBrush}" />
                                </DataTrigger>
                                <DataTrigger Binding="{Binding ProgressText}" Value="失败">
                                    <Setter Property="Foreground" Value="{DynamicResource DangerTextBrush}" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Border>
            <Button Grid.Column="3" Content="{Binding RemoveActionGlyph}" ToolTip="删除此记录"
                    AutomationProperties.Name="删除此记录" Style="{DynamicResource QueueDangerIconButton}"
                    IsEnabled="{Binding CanRemove}" VerticalAlignment="Top" Click="RemoveQueuedProduct_Click" />
            <TextBlock Grid.Row="1" Grid.Column="1" Grid.ColumnSpan="3"
                       Text="{Binding CompletionTimeText}" ToolTip="{Binding CompletionTimeText}"
                       Foreground="{DynamicResource MutedBrush}" FontSize="9.5"
                       HorizontalAlignment="Right" VerticalAlignment="Bottom"
                       Margin="10,5,0,0" TextTrimming="CharacterEllipsis" />
        </Grid>
    </Border>
</DataTemplate>

'''), '    ')
templates = templates[:history_start] + history + templates[history_end:]
write(templates_path, templates)


# Make the popup render regression deterministic: the real one-second queue
# monitor must not consume synthetic rows after Window.Loaded.
ui_path = 'MCPanel.Tests/ReliabilityTests.Ui.cs'
ui = read(ui_path)
method_start = ui.index('    public void DownloadQueuePopupTemplateRendersReadOnlyQueueRows()')
method_end = ui.index('    [TestMethod]\n    public void DownloadQueuePopupDeleteActionReachesMainWindowQueueService()', method_start)
segment = ui[method_start:method_end]
old_show = '            window.Show();\n            window.UpdateLayout();\n'
new_show = '''            var monitoringField = typeof(MainWindow)
                .GetField("_monitoringStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(monitoringField, "测试必须能够关闭主窗体的真实队列监控计时器。");
            monitoringField!.SetValue(window, true);
            window.Show();
            window.UpdateLayout();
'''
if old_show not in segment:
    raise RuntimeError('render regression show anchor not found')
segment = segment.replace(old_show, new_show, 1)
ui = ui[:method_start] + segment + ui[method_end:]
write(ui_path, ui)


# Extend the split-view source regression with timestamp persistence and a
# direct display-format check.
test_path = 'MCPanel.Tests/ReliabilityTests.DownloadQueueTabs.cs'
tests = read(test_path)
source_anchor = '        var templates = ReadDownloadQueueTabsSource(Path.Combine("Resources", "MainWindowTemplates.xaml"));\n'
if '        var queue = ReadDownloadQueueTabsSource("ProductInstallQueue.cs");\n' not in tests:
    tests = replace_once(
        tests,
        source_anchor,
        source_anchor + '        var queue = ReadDownloadQueueTabsSource("ProductInstallQueue.cs");\n',
        'queue source regression variable')

assert_anchor = '        StringAssert.Contains(templates, "Value=\\"失败\\"");\n'
if 'CompletedAtUtc = item.CompletedAtUtc' not in tests:
    timestamp_asserts = '''        StringAssert.Contains(queue, "public DateTime? CompletedAtUtc => _completedAtUtc;");
        StringAssert.Contains(queue, "_completedAtUtc = DateTime.UtcNow;");
        StringAssert.Contains(queue, "CompletedAtUtc = item.CompletedAtUtc");
        StringAssert.Contains(templates, "Text=\\"{Binding CompletionTimeText}\\"");
'''
    tests = replace_once(tests, assert_anchor, assert_anchor + timestamp_asserts, 'timestamp source assertions')

helper_marker = '    private static string ReadDownloadQueueTabsSource(string relativePath)\n'
if 'public void DownloadQueueCompletedHistoryCarriesCompletionTimestamp()' not in tests:
    timestamp_test = '''    [TestMethod]
    public void DownloadQueueCompletedHistoryCarriesCompletionTimestamp()
    {
        var completedUtc = new DateTime(2026, 9, 5, 17, 30, 0, DateTimeKind.Utc);
        var request = new ProductInstallWorkerRequest(
            "TIME001",
            "完成时间测试软件",
            "在线",
            string.Empty,
            ProductSource.Online,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            false);
        var restored = new ProductInstallQueueItemViewModel(
            "time-restored",
            1,
            request,
            ProductInstallQueueStatus.Completed,
            100d,
            "产品文件与运行服务已处理完成。",
            completedUtc);

        Assert.AreEqual(completedUtc, restored.CompletedAtUtc);
        Assert.IsTrue(restored.CompletionTimeText.StartsWith("部署完成 · ", StringComparison.Ordinal));
        StringAssert.Contains(restored.CompletionTimeText, completedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        var live = new ProductInstallQueueItemViewModel(
            "time-live",
            2,
            request,
            ProductInstallQueueStatus.Pending,
            0d,
            "等待处理");
        var before = DateTime.UtcNow.AddSeconds(-1);
        live.SetState(ProductInstallQueueStatus.Completed);
        Assert.IsNotNull(live.CompletedAtUtc);
        Assert.IsTrue(live.CompletedAtUtc >= before && live.CompletedAtUtc <= DateTime.UtcNow.AddSeconds(1));
    }

'''
    tests = replace_once(tests, helper_marker, timestamp_test + helper_marker, 'timestamp regression method')
write(test_path, tests)
