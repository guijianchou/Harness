<#
.SYNOPSIS
Build and verify a self-contained Windows x64 portable release in a new directory.
.NOTES
Run dotnet restore Harness.sln --locked-mode first. Requires PowerShell 7 and .NET 10.
WebView2 Runtime remains a system prerequisite. Existing output is never overwritten.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = 'artifacts/release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'src/Harness/Harness.csproj'
$project = [xml](Get-Content -LiteralPath $projectPath -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $version" }
$output = [IO.Path]::GetFullPath($OutputRoot, $repoRoot)
$releaseDirectory = Join-Path $output $version
if (Test-Path -LiteralPath $releaseDirectory) {
    throw "Release output already exists. Choose a new OutputRoot: $releaseDirectory"
}
$publishDirectory = Join-Path $releaseDirectory 'win-x64'
[IO.Directory]::CreateDirectory($publishDirectory) | Out-Null

Push-Location $repoRoot
try {
    $sourceCommit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
    dotnet publish $projectPath -c Release -p:Platform=x64 -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishDirectory -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
    dotnet publish src/Harness.Updater/Harness.Updater.csproj -c Release -r win-x64 --self-contained true --no-restore `
        -o (Join-Path $publishDirectory 'Updater') -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Portable updater publish failed.' }

    $requiredFiles = @(
        'Harness.exe', 'Harness.dll', 'Harness.Core.dll', 'Harness.pri',
        'Harness.deps.json', 'Harness.runtimeconfig.json',
        'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll',
        'Microsoft.WindowsAppRuntime.dll', 'Microsoft.WindowsAppRuntime.pri',
        'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Controls.dll', 'Microsoft.WinUI.dll',
        'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Core.Projection.dll', 'WebView2Loader.dll',
        'WinRT.Runtime.dll', 'NSec.Cryptography.dll', 'libsodium.dll',
        'System.Security.Cryptography.ProtectedData.dll', 'Assets/WindowIcon.ico', 'Assets/harness-logo.png',
        'Updater/Harness.Updater.exe', 'Updater/Harness.Updater.dll', 'Updater/Harness.Updater.runtimeconfig.json',
        'Updater/Harness.Core.dll', 'Updater/hostfxr.dll', 'Updater/coreclr.dll'
    )
    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $publishDirectory $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Missing or empty release dependency: $relativePath"
        }
    }

    foreach ($name in @('Harness.exe', 'Harness.dll', 'Harness.Core.dll')) {
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory $name)).FileVersion
        if ($fileVersion -ne "$version.0") { throw "Incorrect version in ${name}: $fileVersion" }
    }
    $assembly = [Reflection.Assembly]::LoadFile((Join-Path $publishDirectory 'Harness.dll'))
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src/Harness/Services') -Filter 'HostedUiBridge*.js') {
        $stream = $assembly.GetManifestResourceStream("Harness.Services.$($script.Name)")
        if ($null -eq $stream) { throw "Missing embedded bridge: $($script.Name)" }
        try { $actualHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        finally { $stream.Dispose() }
        if ($actualHash -ne (Get-FileHash -LiteralPath $script.FullName -Algorithm SHA256).Hash) {
            throw "Embedded bridge differs from source: $($script.Name)"
        }
    }
    foreach ($asset in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src/Harness/Assets') -File -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath((Join-Path $repoRoot 'src/Harness'), $asset.FullName)
        $publishedPath = Join-Path $publishDirectory $relativePath
        if ((Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash) {
            throw "Published asset differs from source: $relativePath"
        }
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishDirectory
    $noticesDirectory = Join-Path $publishDirectory 'ThirdPartyNotices'
    [IO.Directory]::CreateDirectory($noticesDirectory) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Harness.Core/OpenClaw/OpenClawSessionRoute.LICENSE.txt') -Destination $noticesDirectory
    $instructions = @"
Harness $version - Windows x64 portable release

Extract the entire ZIP into a new folder and run Harness.exe.
Close the previous Harness instance before launching this version.
Target platform: Windows 11 24H2 (build 26100) or later.
.NET and Windows App SDK are included. Microsoft Edge WebView2 Runtime is required.
Settings and browser profiles remain in %LOCALAPPDATA%\Harness.
Future updates: Settings > About > Check for updates > Download > Save settings and restart to update.
Close other Harness instances first. Downloads use the stable GitHub Release and SHA-256 verification.
The updater keeps the previous folder in a sibling .harness-update-<id>\backup folder.
If startup fails, it restores and restarts the previous version. After a power interruption, close all
Harness instances, keep the failed folder aside, and move that backup back to the original app location.
After confirming the updated app works, you may remove its sibling .harness-update-<id> folder.
OpenClaw Gateway controls are opt-in in Settings; browser authentication is not shared.

请将 ZIP 完整解压到新目录，先退出旧版 Harness，再运行 Harness.exe。
当前目标系统为 Windows 11 24H2（26100）及以上，仍需系统 WebView2 运行时。
原有配置和登录资料保存在 %LOCALAPPDATA%\Harness，不要删除该目录。
后续可在“设置 > 关于”中检查、下载更新，并点击“保存设置并重启更新”。请先关闭其他 Harness 实例。
更新校验 SHA-256 并备份旧版目录，启动失败时恢复旧版。断电等中断后，可关闭所有实例，将失败目录另行保留，
再把相邻 .harness-update-<id>\backup 目录移回原程序位置。确认新版正常后，可自行删除相邻更新备份目录。
OpenClaw 原生控制需在设置中启用并配置凭据，不会自动复用网页认证。

Source: https://github.com/Guijianchou/Harness/tree/$sourceCommit
Release notes: https://github.com/Guijianchou/Harness/releases/tag/$version
"@
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'README.txt'), $instructions, [Text.UTF8Encoding]::new($false))
    $buildInfo = [ordered]@{
        version = $version
        sourceCommit = $sourceCommit
        runtime = 'win-x64'
        targetFramework = [string]$project.Project.PropertyGroup.TargetFramework
        selfContained = $true
        webView2RuntimeIncluded = $false
        portableUpdateSchema = 1
    }
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'BUILD-INFO.json'), ($buildInfo | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    $manifestFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($publishDirectory, $_.FullName).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{ schema = 1; version = $version; runtime = 'win-x64'; files = $manifestFiles }
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'portable-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

    $zipPath = Join-Path $releaseDirectory "Harness-$version-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $files = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
        if ($zip.Entries.Count -ne $files.Count) { throw 'ZIP file count differs from publish output.' }
        foreach ($file in $files) {
            $entryName = [IO.Path]::GetRelativePath($publishDirectory, $file.FullName).Replace('\', '/')
            $entry = $zip.GetEntry($entryName)
            if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "ZIP entry is missing or truncated: $entryName" }
        }
    }
    finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$hash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ Version = $version; Commit = $sourceCommit; Zip = $zipPath; SHA256 = $hash; Files = $files.Count }
}
finally { Pop-Location }
