$ErrorActionPreference = 'Stop'

& (Join-Path $PWD 'tools\Apply-RuntimePortSync-1.3.46-v3.ps1')

$path = Join-Path $PWD 'ProductDeploymentService.cs'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))

# Runtime-discovered product ports may live outside MCPanel's 9000-10000
# auto-allocation pool, but the shared Tomcat listener (8080) must never be
# recovered as a product's independent port. Keep those concepts separate.
$replacements = @(
    @('IsValidObservedRuntimePort(configuredPort.Value)', 'IsValidObservedTomcatProductPort(configuredPort.Value)'),
    @('IsValidObservedRuntimePort(persistedPort)', 'IsValidObservedTomcatProductPort(persistedPort)'),
    @('FirstOrDefault(IsValidObservedRuntimePort)', 'FirstOrDefault(IsValidObservedTomcatProductPort)'),
    @('IsValidObservedRuntimePort(deployment.Port)', 'IsValidObservedTomcatProductPort(deployment.Port)'),
    @('IsValidObservedRuntimePort(port))', 'IsValidObservedTomcatProductPort(port))'),
    @('IsValidObservedRuntimePort(state.Port)', 'IsValidObservedTomcatProductPort(state.Port)'),
    @('IsValidObservedRuntimePort(preferredPort.Value)', 'IsValidObservedTomcatProductPort(preferredPort.Value)')
)

foreach ($pair in $replacements) {
    if (-not $text.Contains($pair[0])) {
        throw "Expected Tomcat runtime-port contract was not found: $($pair[0])"
    }
    $text = $text.Replace($pair[0], $pair[1])
}

$marker = @'
    // Auto-allocation intentionally stays in 9000-10000, but runtime discovery
    // must respect any valid TCP port an administrator configured by hand.
    internal static bool IsValidObservedRuntimePort(int port) => port is > 0 and <= 65535;
'@
$replacement = @'
    // Auto-allocation intentionally stays in 9000-10000, but runtime discovery
    // must respect any valid TCP port an administrator configured by hand.
    internal static bool IsValidObservedRuntimePort(int port) => port is > 0 and <= 65535;

    // 8080 belongs to the shared Tomcat Server. A product can be manually moved
    // to any other valid TCP port (for example 8085, 9314 or 12000), but recovery
    // must never persist the shared listener as an independent product binding.
    internal static bool IsValidObservedTomcatProductPort(int port) =>
        IsValidObservedRuntimePort(port) && port != 8080;
'@
if (-not $text.Contains($marker)) {
    throw 'Observed runtime-port helper marker was not found.'
}
$text = $text.Replace($marker, $replacement)

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
