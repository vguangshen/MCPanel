$ErrorActionPreference = 'Stop'

$path = Join-Path $PWD 'MainWindow.xaml.cs'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))
$actual = @'
            if (!_monitoringStarted)
            {
                return;
            }

            // CPU/memory/drive widgets are only visible on Home. Avoid sampling and
            // raising bindings every second while another page is in front.
            if (IsVisible && WindowState != WindowState.Minimized && HomePage.IsVisible)
            {
                _model.TickSystemState();
            }

            // Runtime discovery is intentionally much slower than visual telemetry:
            // it scans services, ports, install roots and Tomcat/Java state. Suspend
            // it completely while minimized/to-tray and use a faster cadence only
            // while the Environment page is actually visible.
            if (_runtimeRefreshInFlight || !IsVisible || WindowState == WindowState.Minimized)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now < _nextRuntimeRefreshUtc)
'@
$normalized = @'
            if (!_monitoringStarted) return;

            if (IsVisible && WindowState != WindowState.Minimized && HomePage.IsVisible)
            {
                _model.TickSystemState();
            }

            if (_runtimeRefreshInFlight || !IsVisible || WindowState == WindowState.Minimized)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now < _nextRuntimeRefreshUtc)
'@
if (-not $text.Contains($actual)) {
    throw 'Expected MainWindow scheduler block was not found for v2 normalization.'
}
[IO.File]::WriteAllText($path, $text.Replace($actual, $normalized), [Text.UTF8Encoding]::new($false))

& (Join-Path $PWD 'tools\Apply-RuntimePortSync-1.3.46.ps1')
