param([string]$OutputDirectory = 'artifacts/portable-update-regression')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$runRoot = Join-Path $outputRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
dotnet restore (Join-Path $PSScriptRoot 'UpdateProcessFixture/UpdateProcessFixture.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Update fixture restore failed.' }
dotnet build (Join-Path $PSScriptRoot 'UpdateProcessFixture/UpdateProcessFixture.csproj') -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Update fixture build failed.' }
dotnet build (Join-Path $repoRoot 'src/Harness.Updater/Harness.Updater.csproj') -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
$fixtureSource = Join-Path $PSScriptRoot 'UpdateProcessFixture/bin/Debug/net10.0/win-x64'
$helperSource = Join-Path $repoRoot 'src/Harness.Updater/bin/Debug/net10.0/win-x64'
Add-Type -Path (Join-Path $helperSource 'Harness.Core.dll')

function Write-Json($path, $value) { [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false)) }
function Wait-File($path, $seconds = 40) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while (-not (Test-Path -LiteralPath $path)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for $path" }
        Start-Sleep -Milliseconds 100
    }
}
function Start-Hidden($executable, $arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = 'Hidden'
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($executable)
    $info.Environment.Remove('HARNESS_UPDATE_JOB') | Out-Null
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}
function Make-Package($directory, $version, $mode) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    Copy-Item -Path (Join-Path $fixtureSource '*') -Destination $directory -Recurse
    $updater = Join-Path $directory 'Updater'
    [IO.Directory]::CreateDirectory($updater) | Out-Null
    Copy-Item -Path (Join-Path $helperSource '*') -Destination $updater -Recurse
    foreach ($name in @('Harness.pri', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll', 'BUILD-INFO.json')) {
        [IO.File]::WriteAllText((Join-Path $directory $name), "fixture $version")
    }
    foreach ($name in @('hostfxr.dll', 'coreclr.dll')) {
        Copy-Item -LiteralPath (Join-Path $helperSource $name) -Destination (Join-Path $directory $name)
    }
    [IO.File]::WriteAllText((Join-Path $directory 'fixture-mode.txt'), $mode)
    $files = @(Get-ChildItem -LiteralPath $directory -Recurse -File | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    Write-Json (Join-Path $directory 'portable-manifest.json') @{ schema = 1; version = $version; runtime = 'win-x64'; files = $files }
}

$results = @()
foreach ($mode in @('healthy', 'fail', 'busy', 'blocked-result')) {
    $case = Join-Path $runRoot $mode
    [IO.Directory]::CreateDirectory($case) | Out-Null
    $installation = Join-Path $case 'Harness portable test'
    $job = Join-Path $case ('.harness-update-' + [Guid]::NewGuid().ToString('N'))
    $runner = Join-Path $job 'runner'
    [IO.Directory]::CreateDirectory($runner) | Out-Null
    Copy-Item -Path (Join-Path $helperSource '*') -Destination $runner -Recurse
    Make-Package $installation '5.3.8' 'old'
    Make-Package (Join-Path $job 'payload') '5.10.0' $mode
    # Fixtures created in a Low workspace must start at the same level as real prepared apphosts.
    & (Join-Path $env:SystemRoot 'System32/icacls.exe') (Join-Path $installation 'Harness.exe') /setintegritylevel M | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the isolated parent fixture integrity label.' }
    [IO.File]::WriteAllText((Join-Path $installation 'personal-note.txt'), 'Preserve this file')
    $ready = Join-Path $case 'parent-ready'
    $stop = Join-Path $case 'stop-parent'
    $parent = Start-Hidden (Join-Path $installation 'Harness.exe') @('--parent', $ready, $stop)
    $helper = $null
    $otherInstance = $null
    try {
        Wait-File $ready
        if ($mode -eq 'busy') {
            $otherReady = Join-Path $case 'other-ready'
            $otherStop = Join-Path $case 'stop-other'
            $otherInstance = Start-Hidden (Join-Path $installation 'Harness.exe') @('--parent', $otherReady, $otherStop)
            Wait-File $otherReady
        }
        Write-Json (Join-Path $job 'plan.json') @{ installation = $installation; currentVersion = '5.3.8'; version = '5.10.0'; parentProcessId = $parent.Id; parentStartTicks = $parent.StartTime.ToUniversalTime().Ticks }
        [Harness.Updates.PortableUpdateIntegrity]::NormalizePreparedAppHosts($job)
        $helper = Start-Hidden (Join-Path $runner 'Harness.Updater.exe') @($job)
        Wait-File (Join-Path $job 'helper-ready.json')
        if (-not (Test-Path -LiteralPath (Join-Path $installation 'Harness.exe'))) { throw 'Helper changed files before parent exit.' }
        if ($mode -eq 'blocked-result') { [IO.Directory]::CreateDirectory((Join-Path $job 'result.json')) | Out-Null }
        [IO.File]::WriteAllText($stop, 'stop')
        if (-not $helper.WaitForExit(60000)) { throw 'Updater did not complete.' }
        if ($mode -eq 'blocked-result') {
            Wait-File (Join-Path $installation 'restarted.txt')
            $installed = Get-Content -LiteralPath (Join-Path $installation 'portable-manifest.json') -Raw | ConvertFrom-Json
            if ($installed.version -ne '5.3.8' -or $helper.ExitCode -eq 0) { throw 'A blocked result file must leave and restart the old installation.' }
            $results += [pscustomobject]@{ Case = $mode; Result = 'PreviousVersionRestarted'; ExitCode = $helper.ExitCode }
            Write-Output 'PASS: result write failure does not prevent restarting the previous version'
            continue
        }
        $result = Get-Content -LiteralPath (Join-Path $job 'result.json') -Raw | ConvertFrom-Json
        $expected = if ($mode -eq 'healthy') { 'Completed' } elseif ($mode -eq 'busy') { 'Failed' } else { 'RolledBack' }
        if ($result.state -ne $expected) { throw "Expected $expected, got $($result | ConvertTo-Json -Compress)" }
        if ((Get-Content -LiteralPath (Join-Path $installation 'personal-note.txt') -Raw) -ne 'Preserve this file') { throw 'User file was lost.' }
        if ($mode -eq 'fail') { Wait-File (Join-Path $installation 'restarted.txt') }
        elseif ($mode -eq 'healthy') {
            $health = Get-Content -LiteralPath (Join-Path $job 'healthy.json') -Raw | ConvertFrom-Json
            $candidate = Get-Process -Id $health.processId -ErrorAction SilentlyContinue
            if ($null -ne $candidate) {
                if (-not $candidate.WaitForExit(10000)) { throw 'Candidate fixture did not finish.' }
                $candidate.Dispose()
            }
        }
        else {
            if ($otherInstance.HasExited -or $result.errorCode -ne 'OtherInstances') { throw 'Updater did not preserve the other running instance.' }
            $installed = Get-Content -LiteralPath (Join-Path $installation 'portable-manifest.json') -Raw | ConvertFrom-Json
            if ($installed.version -ne '5.3.8') { throw 'Updater replaced an installation still in use.' }
            [IO.File]::WriteAllText($otherStop, 'stop')
            if (-not $otherInstance.WaitForExit(5000)) { throw 'Second instance fixture did not finish.' }
        }
        $results += [pscustomobject]@{ Case = $mode; Result = $result.state; ExitCode = $helper.ExitCode }
        Write-Output "PASS: real portable updater process: $mode -> $expected"
    }
    finally {
        foreach ($process in @($helper, $parent, $otherInstance)) {
            if ($null -ne $process) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
        }
    }
}
Write-Json (Join-Path $outputRoot 'results.json') $results
Write-Output "PASS: updater process exit, startup acknowledgement, rollback, restart, and user file preservation. Evidence: $runRoot"
