param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this verifier with pwsh -STA.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$coreProject = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness.Core/Harness.Core.csproj') -Raw)
$coreVersion = $coreProject.Project.PropertyGroup.Version
$corePath = Join-Path $repoRoot "bin/Debug/$coreVersion/net10.0/Harness.Core.dll"
$lock = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/packages.lock.json') -Raw | ConvertFrom-Json
$webViewVersion = $null
foreach ($framework in $lock.dependencies.PSObject.Properties) {
    $package = $framework.Value.PSObject.Properties['Microsoft.Web.WebView2']
    if ($null -ne $package) { $webViewVersion = $package.Value.resolved; break }
}
if (!$webViewVersion) { throw 'WebView2 version was not found in the package lock.' }
$sdk = Join-Path $env:USERPROFILE ".nuget/packages/microsoft.web.webview2/$webViewVersion"
$nativeCorePath = Join-Path $sdk 'lib/net462/Microsoft.Web.WebView2.Core.dll'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path $nativeCorePath
Add-Type -Path $corePath
$protectedDataPath = Join-Path $env:USERPROFILE '.nuget/packages/system.security.cryptography.protecteddata/10.0.8/lib/net10.0/System.Security.Cryptography.ProtectedData.dll'
Add-Type -Path $protectedDataPath
$nsecPath = Join-Path $env:USERPROFILE '.nuget/packages/nsec.cryptography/26.4.0/lib/net9.0/NSec.Cryptography.dll'
$sodiumPath = Join-Path $env:USERPROFILE '.nuget/packages/libsodium/1.0.22/runtimes/win-x64/native/libsodium.dll'
[Runtime.InteropServices.NativeLibrary]::Load($sodiumPath) | Out-Null
Add-Type -Path $nsecPath
Add-Type -TypeDefinition @'
public static class HarnessRegressionResource {
    public static System.Func<System.IO.Stream> Open(string path) => () => System.IO.File.OpenRead(path);
}
'@

$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$sourceFiles = @(
    'src/Harness/Services/WebViewCommandScripts.cs',
    'src/Harness/Services/WebViewScriptExecutor.cs',
    'src/Harness/Services/WebViewMessageOwnership.cs',
    'src/Harness/Services/WebViewService.ErrorPage.cs',
    'src/Harness/Services/WebViewService.HostMessages.cs',
    'src/Harness/Services/SettingsPersistenceAdapter.cs',
    'src/Harness/Services/StartupRegistrationService.cs',
    'src/Harness/ViewModels/SettingsViewModel.cs',
    'src/Harness/ViewModels/SettingsViewModel.Appearance.cs',
    'src/Harness/ViewModels/SettingsViewModel.Gateway.cs',
    'src/Harness/Services/OpenClawCredentialStore.cs',
    'src/Harness/Services/OpenClawDeviceIdentityService.cs',
    'src/Harness/ViewModels/MainViewModel.Environment.cs',
    'tools/NativeWebViewRegression.cs'
)
foreach ($relativePath in $sourceFiles) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw
    # The standalone controller uses the .NET SDK projection of the same WebView2 COM runtime.
    $source = $source.Replace('using Windows.Foundation;', '')
    $source = $source.Replace('TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>', 'EventHandler<CoreWebView2WebMessageReceivedEventArgs>')
    $source = $source.Replace('OnWebMessageReceived(sender, args, hostGeneration)', 'OnWebMessageReceived((CoreWebView2)sender, args, hostGeneration)')
    $source = $source.Replace('.AsTask(cancellationToken)', '.WaitAsync(cancellationToken)')
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $parseOptions))
}
$methods = [Collections.Generic.List[string]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/Services/WebViewService.cs'; Names = @('Reload', 'RetryNavigation') },
    @{ Path = 'src/Harness/Services/WebViewService.Navigation.cs'; Names = @('OnNavigationStarting') },
    @{ Path = 'src/Harness/Services/WebViewService.ControlUiInspection.cs'; Names = @('ApplyControlUiSnapshot') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $parseOptions)
    foreach ($method in $tree.GetRoot().DescendantNodes()) {
        if ($method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $method.Identifier.ValueText -in $selection.Names) {
            $methods.Add($method.ToFullString())
        }
    }
}
$serviceMethods = 'using Microsoft.Web.WebView2.Core; namespace Harness.Services { public partial class WebViewService {' + ($methods -join "`n") + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($serviceMethods, $parseOptions))
$propertiesTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/ViewModels/MainViewModel.Core.Properties.cs') -Raw), $parseOptions)
$selectionProperty = $propertiesTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'SelectedEnvironment' } | Select-Object -First 1
$selectionSource = 'using Harness.Models; namespace Harness.ViewModels { public partial class MainViewModel {' + $selectionProperty.ToFullString() + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($selectionSource, $parseOptions))
$resourceNames = @($trees | ForEach-Object { $_.GetRoot().DescendantNodes() } | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax] -and $_.Expression.ToString() -eq 'StringResources' } | ForEach-Object { $_.Name.Identifier.ValueText } | Sort-Object -Unique)
$resourceProperties = ($resourceNames | Where-Object { $_ -notin @('Get', 'Load', 'Retry', 'Settings', 'MainStatusLabel') } | ForEach-Object { 'public static string ' + $_ + ' => Get("' + $_ + '");' }) -join "`n"
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('namespace Harness.Helpers { public static partial class StringResources {' + $resourceProperties + '} }', $parseOptions))
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;', $parseOptions))

$referencePaths = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName)
$referencePaths += @($corePath, $nativeCorePath, $protectedDataPath, $nsecPath)
foreach ($name in @('System.Windows.Forms.dll', 'System.Windows.Forms.Primitives.dll', 'System.Drawing.Common.dll', 'System.Private.Windows.Core.dll')) {
    $referencePaths += Join-Path $PSHOME $name
}
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@($referencePaths | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('HarnessNativeRegression_' + [Guid]::NewGuid().ToString('N'), $trees, $references, $options)
$resources = [Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
$project = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/Harness.csproj') -Raw)
foreach ($resource in $project.SelectNodes('//EmbeddedResource')) {
    $path = Join-Path (Join-Path $repoRoot 'src/Harness') $resource.Include
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new($resource.LogicalName, [HarnessRegressionResource]::Open($path), $true))
}
$stream = [IO.MemoryStream]::new()
$emit = $compilation.Emit($stream, $null, $null, $null, $resources)
if (!$emit.Success) {
    $emit.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { $_.ToString() }
    throw 'Native regression compilation failed.'
}
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$run = $assembly.GetType('HarnessNativeRegression').GetMethod('Run').CreateDelegate([Action[string,string,string]])
try {
    $run.Invoke((Join-Path $sdk 'runtimes/win-x64/native'), $repoRoot, $output)
} catch {
    throw $_.Exception.GetBaseException()
}
