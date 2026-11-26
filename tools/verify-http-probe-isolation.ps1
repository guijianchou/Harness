Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$factory = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness.Core/Services/StatelessProbeHttpClient.cs') -Raw
if ($factory -notmatch 'UseCookies\s*=\s*false' -or $factory -notmatch 'AllowAutoRedirect\s*=\s*false') {
    throw 'HTTP probe clients must not retain cookies or follow redirects between environments.'
}

foreach ($path in @(
    'src/Harness.Core/Services/ControlUiLatencyService.cs',
    'src/Harness/Services/DiagnosticService.cs',
    'src/Harness/Services/GatewayHeartbeatTransport.cs'
)) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw
    if ($source -notmatch 'StatelessProbeHttpClient\.Create\(') {
        throw "Default HTTP probes must use the stateless client factory: $path"
    }
    # The latency constructor accepting an injected test/custom transport remains supported.
    $source = $source.Replace('new HttpClient(messageHandler)', 'InjectedHttpClient')
    if ($source -match 'new HttpClient\b|HttpClientHandler|HttpClient\s+\w+\s*=\s*new\(') {
        throw "HTTP probe client construction bypasses the shared factory: $path"
    }
}

Write-Output 'HTTP probe isolation guardrail passed: latency, diagnostics and heartbeat use the stateless factory.'
