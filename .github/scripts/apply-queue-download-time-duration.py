from pathlib import Path


def read(path):
    return Path(path).read_text(encoding='utf-8-sig').replace('\r\n', '\n')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError(label + ' anchor not found')
    return text.replace(old, new, 1)


queue_path = 'ProductInstallQueue.cs'
queue = read(queue_path)

queue = replace_once(
    queue,
    '    private DateTime? _completedAtUtc;\n',
    '    private DateTime? _completedAtUtc;\n    private DateTime? _requestedAtUtc;\n',
    'requested timestamp field')

queue = replace_once(
    queue,
    '''    internal ProductInstallQueueItemViewModel(
        string queueId,
        int sequence,
        ProductInstallWorkerRequest request,
        ProductInstallQueueStatus state,
        double progress,
        string message,
        DateTime? completedAtUtc = null)
''',
    '''    internal ProductInstallQueueItemViewModel(
        string queueId,
        int sequence,
        ProductInstallWorkerRequest request,
        ProductInstallQueueStatus state,
        double progress,
        string message,
        DateTime? completedAtUtc = null,
        DateTime? requestedAtUtc = null)
''',
    'queue item constructor')

queue = replace_once(
    queue,
    '        _completedAtUtc = ProductInstallQueueService.IsTerminal(state) ? completedAtUtc : null;\n',
    '        _completedAtUtc = ProductInstallQueueService.IsTerminal(state) ? completedAtUtc : null;\n        _requestedAtUtc = requestedAtUtc;\n',
    'constructor requested timestamp assignment')

queue = replace_once(
    queue,
    '''        var item = new ProductInstallQueueItemViewModel(
            queueId,
            sequence,
            request,
            ProductInstallQueueStatus.Pending,
            0,
            $"已加入安装队列，等待第 {sequence} 项处理。");
''',
    '''        var item = new ProductInstallQueueItemViewModel(
            queueId,
            sequence,
            request,
            ProductInstallQueueStatus.Pending,
            0,
            $"已加入安装队列，等待第 {sequence} 项处理。",
            requestedAtUtc: DateTime.UtcNow);
''',
    'enqueue requested timestamp')

queue = replace_once(
    queue,
    '''            var item = new ProductInstallQueueItemViewModel(
                persisted.QueueId,
                persisted.Sequence,
                persisted.Request,
                state,
                restoredProgress,
                message,
                isTerminal
                    ? persisted.CompletedAtUtc ?? TryGetCompletionTimeUtc(persisted.ProgressPath)
                    : null);
''',
    '''            var item = new ProductInstallQueueItemViewModel(
                persisted.QueueId,
                persisted.Sequence,
                persisted.Request,
                state,
                restoredProgress,
                message,
                completedAtUtc: isTerminal
                    ? persisted.CompletedAtUtc ?? TryGetCompletionTimeUtc(persisted.ProgressPath)
                    : null,
                requestedAtUtc: persisted.RequestedAtUtc ?? TryGetRequestedTimeUtc(persisted.ProgressPath));
''',
    'restore requested timestamp')

queue = replace_once(
    queue,
    '''    private void StopPreviousSession(string? previousSessionId)
''',
    '''    private static DateTime? TryGetRequestedTimeUtc(string? progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath))
        {
            return null;
        }

        try
        {
            return File.Exists(progressPath) ? File.GetCreationTimeUtc(progressPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private void StopPreviousSession(string? previousSessionId)
''',
    'legacy requested timestamp helper')

queue = replace_once(
    queue,
    '''                    {
                        IsRemovalRequested = item.IsRemovalRequested,
                        CompletedAtUtc = item.CompletedAtUtc
                    })
''',
    '''                    {
                        IsRemovalRequested = item.IsRemovalRequested,
                        CompletedAtUtc = item.CompletedAtUtc,
                        RequestedAtUtc = item.RequestedAtUtc
                    })
''',
    'persist requested timestamp')

old_timing_props = '''    public string Message { get => _message; private set => SetProperty(ref _message, value); }
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
new_timing_props = '''    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public DateTime? CompletedAtUtc => _completedAtUtc;
    public DateTime? RequestedAtUtc => _requestedAtUtc;
    public string DownloadTimeText => RequestedAtUtc is DateTime requestedAtUtc
        ? $"下载时间 · {requestedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "下载时间未记录";
    public string CompletionTimeText
    {
        get
        {
            if (!IsTerminal)
            {
                return string.Empty;
            }

            var label = State == ProductInstallQueueStatus.Completed ? "部署完成" : "任务结束";
            if (CompletedAtUtc is not DateTime completedAtUtc)
            {
                return $"{label}时间未记录";
            }

            var durationPrefix = RequestedAtUtc is DateTime requestedAtUtc && completedAtUtc >= requestedAtUtc
                ? $"用时 {FormatElapsedDuration(completedAtUtc - requestedAtUtc)} · "
                : string.Empty;
            return $"{durationPrefix}{label} · {completedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
    }
'''
queue = replace_once(queue, old_timing_props, new_timing_props, 'timing display properties')

queue = replace_once(
    queue,
    '''    private static string ResolveProductName(ProductInstallWorkerRequest request)
''',
    '''    private static string FormatElapsedDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalDays >= 1)
        {
            return $"{(int)elapsed.TotalDays}天{elapsed.Hours:D2}小时{elapsed.Minutes:D2}分";
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}小时{elapsed.Minutes:D2}分";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{(int)elapsed.TotalMinutes}分{elapsed.Seconds:D2}秒";
        }

        return $"{Math.Max(0, (int)elapsed.TotalSeconds)}秒";
    }

    private static string ResolveProductName(ProductInstallWorkerRequest request)
''',
    'duration formatter')

queue = replace_once(
    queue,
    '''{
    public bool IsRemovalRequested { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
}
''',
    '''{
    public bool IsRemovalRequested { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? RequestedAtUtc { get; init; }
}
''',
    'persisted requested timestamp property')

write(queue_path, queue)


templates_path = 'Resources/MainWindowTemplates.xaml'
templates = read(templates_path)
active_start = templates.index('    <DataTemplate x:Key="InstallationQueueItemTemplate">')
active_end = templates.index('    <DataTemplate x:Key="InstallationQueueHistoryItemTemplate">', active_start)
active = templates[active_start:active_end]

active = replace_once(
    active,
    '''                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
''',
    '''                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
''',
    'active row definitions')

active = replace_once(
    active,
    '''                <TextBlock Grid.Row="3"
                           Grid.ColumnSpan="5"
                           Text="{Binding TransferText}"
                           ToolTip="{Binding TransferText}"
                           Foreground="{DynamicResource PrimaryBrush}"
                           FontSize="10"
                           Margin="62,4,0,0"
                           TextTrimming="CharacterEllipsis" />
''',
    '''                <TextBlock Grid.Row="3"
                           Grid.ColumnSpan="5"
                           Text="{Binding TransferText}"
                           ToolTip="{Binding TransferText}"
                           Foreground="{DynamicResource PrimaryBrush}"
                           FontSize="10"
                           Margin="62,4,0,0"
                           TextTrimming="CharacterEllipsis" />
                <TextBlock Grid.Row="4"
                           Grid.ColumnSpan="5"
                           Text="{Binding DownloadTimeText}"
                           ToolTip="{Binding DownloadTimeText}"
                           Foreground="{DynamicResource MutedBrush}"
                           FontSize="9.5"
                           HorizontalAlignment="Right"
                           Margin="62,4,0,0"
                           TextTrimming="CharacterEllipsis" />
''',
    'active download time row')

templates = templates[:active_start] + active + templates[active_end:]
write(templates_path, templates)


test_path = 'MCPanel.Tests/ReliabilityTests.DownloadQueueTabs.cs'
tests = read(test_path)

tests = replace_once(
    tests,
    '        StringAssert.Contains(templates, "Text=\\"{Binding CompletionTimeText}\\"");\n',
    '        StringAssert.Contains(templates, "Text=\\"{Binding CompletionTimeText}\\"");\n        StringAssert.Contains(templates, "Text=\\"{Binding DownloadTimeText}\\"");\n',
    'active timing template assertion')

tests = replace_once(
    tests,
    '        StringAssert.Contains(queue, "public DateTime? CompletedAtUtc => _completedAtUtc;");\n',
    '        StringAssert.Contains(queue, "public DateTime? CompletedAtUtc => _completedAtUtc;");\n        StringAssert.Contains(queue, "public DateTime? RequestedAtUtc => _requestedAtUtc;");\n',
    'requested timestamp source assertion')

tests = replace_once(
    tests,
    '        StringAssert.Contains(queue, "CompletedAtUtc = item.CompletedAtUtc");\n',
    '        StringAssert.Contains(queue, "CompletedAtUtc = item.CompletedAtUtc");\n        StringAssert.Contains(queue, "RequestedAtUtc = item.RequestedAtUtc");\n        StringAssert.Contains(queue, "requestedAtUtc: DateTime.UtcNow");\n',
    'requested timestamp persistence assertions')

method_start = tests.index('    [TestMethod]\n    public void DownloadQueueCompletedHistoryCarriesCompletionTimestamp()')
method_end = tests.index('    private static string ReadDownloadQueueTabsSource(string relativePath)', method_start)
new_test = '''    [TestMethod]
    public void DownloadQueueTimingShowsRequestTimeAndElapsedDuration()
    {
        var completedUtc = new DateTime(2026, 9, 5, 17, 30, 0, DateTimeKind.Utc);
        var requestedUtc = completedUtc.AddMinutes(-7).AddSeconds(-25);
        var request = new ProductInstallWorkerRequest(
            "TIME001",
            "下载时间测试软件",
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
            completedUtc,
            requestedUtc);

        Assert.AreEqual(requestedUtc, restored.RequestedAtUtc);
        Assert.AreEqual(completedUtc, restored.CompletedAtUtc);
        StringAssert.Contains(restored.DownloadTimeText, requestedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Assert.IsTrue(restored.CompletionTimeText.StartsWith("用时 7分25秒 · 部署完成 · ", StringComparison.Ordinal));
        StringAssert.Contains(restored.CompletionTimeText, completedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        var active = new ProductInstallQueueItemViewModel(
            "time-active",
            2,
            request,
            ProductInstallQueueStatus.Pending,
            0d,
            "等待处理",
            requestedAtUtc: requestedUtc);
        Assert.AreEqual(requestedUtc, active.RequestedAtUtc);
        StringAssert.Contains(active.DownloadTimeText, requestedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Assert.AreEqual(string.Empty, active.CompletionTimeText);

        var live = new ProductInstallQueueItemViewModel(
            "time-live",
            3,
            request,
            ProductInstallQueueStatus.Pending,
            0d,
            "等待处理",
            requestedAtUtc: DateTime.UtcNow.AddSeconds(-2));
        var before = DateTime.UtcNow.AddSeconds(-1);
        live.SetState(ProductInstallQueueStatus.Completed);
        Assert.IsNotNull(live.CompletedAtUtc);
        Assert.IsTrue(live.CompletedAtUtc >= before && live.CompletedAtUtc <= DateTime.UtcNow.AddSeconds(1));
        Assert.IsTrue(live.CompletionTimeText.StartsWith("用时 ", StringComparison.Ordinal));
    }

'''
tests = tests[:method_start] + new_test + tests[method_end:]
write(test_path, tests)
