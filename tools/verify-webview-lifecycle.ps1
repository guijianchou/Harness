param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$methods = [Collections.Generic.List[string]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/Services/WebViewService.Lifecycle.cs'; Names = @('InitializeAsync', 'WaitForInitializationOperationAsync', 'DetachCurrentWebView', 'TryGetCoreWebView2', 'GetCoreWebView', 'IsCurrentInitialization', 'IsCurrentHost', 'IsCurrentNavigation') },
    @{ Path = 'src/Harness/Services/WebViewService.ExternalContent.cs'; Names = @('OnNewWindowRequested', 'IsSameOriginAsCurrentSession') },
    @{ Path = 'src/Harness/Services/WebViewService.cs'; Names = @('Navigate', 'RetryNavigation') },
    @{ Path = 'src/Harness/Services/WebViewService.NavigationCommands.cs'; Names = @('TryNavigateCoreWebView') },
    @{ Path = 'src/Harness/Services/WebViewService.Navigation.cs'; Names = @('OnNavigationCompleted') },
    @{ Path = 'src/Harness/Services/WebViewService.NavigationRecovery.cs'; Names = @('OnProcessFailed') },
    @{ Path = 'src/Harness/Services/WebViewService.NavigationState.cs'; Names = @('CancelActiveNavigation', 'PrepareNavigationStart') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $parseOptions)
    $found = @()
    foreach ($method in $tree.GetRoot().DescendantNodes()) {
        if ($method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $method.Identifier.ValueText -in $selection.Names) {
            $found += $method.Identifier.ValueText
            # Make the event entry awaitable; its body remains the production method.
            $methods.Add($method.ToFullString().Replace('private async void OnNavigationCompleted', 'private async Task OnNavigationCompleted'))
        }
    }
    foreach ($name in $selection.Names) {
        if ($name -notin $found) { throw "Missing production method $name in $($selection.Path)" }
    }
}
$serviceTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/Services/WebViewService.cs') -Raw), $parseOptions)
foreach ($name in @('IsInitialized', 'RequiresHostRecreation')) {
    $property = @($serviceTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq $name })
    if ($property.Count -ne 1) { throw "Expected one production property $name in WebViewService.cs" }
    $methods.Add($property[0].ToFullString())
}
$source = 'using System.Runtime.InteropServices; using Harness.Helpers; using Microsoft.UI.Xaml.Controls; using Microsoft.Web.WebView2.Core; namespace Harness.Services { public partial class WebViewService {' + ($methods -join "`n") + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $parseOptions))
$viewModelMethods = [Collections.Generic.List[string]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/ViewModels/MainViewModel.Lifecycle.cs'; Names = @('OnHostRecreationRequired') },
    @{ Path = 'src/Harness/ViewModels/MainViewModel.Commands.cs'; Names = @('TryRequestManualHostRecreation', 'OnRetry', 'OnReload') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $parseOptions)
    $found = @()
    foreach ($method in $tree.GetRoot().DescendantNodes()) {
        if ($method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $method.Identifier.ValueText -in $selection.Names) {
            $found += $method.Identifier.ValueText
            $viewModelMethods.Add($method.ToFullString())
        }
    }
    foreach ($name in $selection.Names) {
        if ($name -notin $found) { throw "Missing production method $name in $($selection.Path)" }
    }
}
$viewModelSource = 'namespace Harness.ViewModels { public partial class MainViewModel {' + ($viewModelMethods -join "`n") + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($viewModelSource, $parseOptions))
$windowMethods = [Collections.Generic.List[string]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/MainWindow.WebView.cs'; Names = @('ScheduleWebViewRecreation', 'RecreateWebViewAsync', 'CanContinueWebViewRecreation', 'ScheduleDeferredWebViewRecreationRetry', 'ClearWebViewHostForPlaceholderEnvironment') },
    @{ Path = 'src/Harness/MainWindow.Commands.cs'; Names = @('CancelAutomaticWebViewRecovery') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $parseOptions)
    $found = @()
    foreach ($method in $tree.GetRoot().DescendantNodes()) {
        if ($method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $method.Identifier.ValueText -in $selection.Names) {
            $found += $method.Identifier.ValueText
            $windowMethods.Add($method.ToFullString())
        }
    }
    foreach ($name in $selection.Names) {
        if ($name -notin $found) { throw "Missing production method $name in $($selection.Path)" }
    }
}
$windowSource = 'using System.Linq; using Harness.Helpers; using Harness.Services; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Controls; namespace Harness { public partial class MainWindow {' + ($windowMethods -join "`n") + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($windowSource, $parseOptions))
foreach ($relativePath in @(
    'src/Harness/Services/UiTaskDispatcher.cs',
    'src/Harness/Services/WebViewGenerationTracker.cs',
    'src/Harness/Services/WebViewRecreationService.cs',
    'src/Harness.Core/Services/WebViewCircuitBreaker.cs',
    'tools/WebViewLifecycleRegression.cs'
)) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw), $parseOptions))
}
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('global using System; global using System.IO; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;', $parseOptions))
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll') | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_.FullName) })
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('HarnessLifecycleRegression_' + [Guid]::NewGuid().ToString('N'), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
$emit = $compilation.Emit($stream)
if (!$emit.Success) {
    $emit.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { $_.ToString() }
    throw 'WebView lifecycle regression compilation failed.'
}
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$run = $assembly.GetType('WebViewLifecycleRegression').GetMethod('RunAsync').CreateDelegate([Func[Threading.Tasks.Task]])
$run.Invoke().GetAwaiter().GetResult()
