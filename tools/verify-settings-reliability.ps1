Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$coreProject = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness.Core/Harness.Core.csproj') -Raw)
$coreVersion = $coreProject.Project.PropertyGroup.Version
$corePath = Join-Path $repoRoot "bin/Debug/$coreVersion/net10.0/Harness.Core.dll"
if (-not (Test-Path -LiteralPath $corePath)) { throw 'Build Harness.Core in Debug before running this verifier.' }
Add-Type -Path $corePath
$appProject = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/Harness.csproj') -Raw)
$protectedDataVersion = $appProject.SelectSingleNode('//PackageReference[@Include="System.Security.Cryptography.ProtectedData"]').Version
$nugetCache = & dotnet nuget locals global-packages --list --force-english-output
if ($LASTEXITCODE -ne 0 -or $nugetCache -notmatch '^global-packages:\s*(.+)$') { throw 'Could not resolve the NuGet global-packages directory.' }
$protectedDataPath = Join-Path $Matches[1].Trim() "system.security.cryptography.protecteddata/$protectedDataVersion/lib/net10.0/System.Security.Cryptography.ProtectedData.dll"
Add-Type -Path $protectedDataPath
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($path in @(
    'src/Harness/Services/SettingsPersistenceAdapter.cs',
    'src/Harness/Services/LiveShellSettingsApplier.cs',
    'src/Harness/Services/StartupRegistrationService.cs',
    'src/Harness/Services/OpenClawCredentialStore.cs',
    'src/Harness/ViewModels/SettingsViewModel.cs',
    'src/Harness/ViewModels/SettingsViewModel.Appearance.cs',
    'src/Harness/ViewModels/SettingsViewModel.Gateway.cs',
    'tools/SettingsReliabilityRegression.cs'
)) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), $parseOptions))
}
$methods = [Collections.Generic.List[string]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/Views/SettingsDialog.Actions.cs'; Names = @('OnSaveClick', 'SaveSettingsAsync', 'OnCancelClick', 'RefreshStartupRegistrationStatus', 'ObserveStartupRegistrationStatusAsync', 'OnRunDiagnosticsClick', 'OnViewLogsClick', 'OnDevToolsClick', 'OnExportDiagnosticBundleClick', 'ExecuteDeveloperCommand', 'ClearEnvironmentSessionAsync', 'GetSavedSessionEnvironment', 'TryApplyEdit') },
    @{ Path = 'src/Harness/Views/SettingsDialog.Initialization.cs'; Names = @('OnSettingsAppWindowClosing') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $parseOptions)
    $found = 0
    foreach ($method in $tree.GetRoot().DescendantNodes()) {
        if ($method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $method.Identifier.ValueText -in $selection.Names) {
            $methods.Add($method.ToFullString())
            $found++
        }
    }
    if ($found -ne $selection.Names.Count) { throw "Missing authored Settings method in $($selection.Path)." }
}
$source = 'using Harness.Helpers; using Harness.Models; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Controls; namespace Harness.Views { public sealed partial class SettingsDialog {' + ($methods -join "`n") + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $parseOptions))
$sharedTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/Views/SettingsDialog.Shared.cs') -Raw), $parseOptions)
$savedSessionsProperty = @($sharedTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'SavedSessionEnvironments' })
if ($savedSessionsProperty.Count -ne 1) { throw 'Expected saved-session environment snapshot property.' }
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('using Harness.Models; namespace Harness.Views { public sealed partial class SettingsDialog {' + $savedSessionsProperty[0].ToFullString() + '} }', $parseOptions))
$exitMethods = [Collections.Generic.List[string]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/MainWindow.Lifecycle.cs'; Names = @('DeferExitUntilSettingsSaved', 'ExitAfterSettingsSaveAsync') },
    @{ Path = 'src/Harness/MainWindow.Tray.cs'; Names = @('RequestApplicationExit') },
    @{ Path = 'src/Harness/MainWindow.Commands.cs'; Names = @('InitializeSettingsSynchronization', 'OnSettingsCommitted', 'ApplyCommittedSettings', 'HandleSettingsSaved', 'OnSettingsSaved') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $parseOptions)
    $found = 0
    foreach ($method in $tree.GetRoot().DescendantNodes()) {
        if ($method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $method.Identifier.ValueText -in $selection.Names) {
            $exitMethods.Add($method.ToFullString())
            $found++
        }
    }
    if ($found -ne $selection.Names.Count) { throw "Missing authored exit method in $($selection.Path)." }
}
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('using Harness.Models; namespace Harness { public sealed partial class MainWindow {' + ($exitMethods -join "`n") + '} }', $parseOptions))
$profileTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/Services/WebViewService.Profile.cs') -Raw), $parseOptions)
$deleteProfile = @($profileTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'DeleteUserDataFolderForEnvironment' })
if ($deleteProfile.Count -ne 1) { throw 'Expected the production profile deletion method.' }
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('namespace Harness.Services { public static partial class WebViewService {' + $deleteProfile[0].ToFullString() + '} }', $parseOptions))
$environmentTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/ViewModels/MainViewModel.Environment.cs') -Raw), $parseOptions)
$environmentMethods = @($environmentTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('UpdateEnvironmentSelection', 'ApplyPlaceholderEnvironmentState') })
if ($environmentMethods.Count -ne 2) { throw 'Expected production environment selection methods.' }
$propertiesTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/ViewModels/MainViewModel.Core.Properties.cs') -Raw), $parseOptions)
$placeholderProperty = @($propertiesTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsPlaceholderEnvironment' })
if ($placeholderProperty.Count -ne 1) { throw 'Expected the production placeholder predicate.' }
$environmentSource = 'using Harness.Models; using Harness.Helpers; namespace Harness.ViewModels { public partial class MainViewModel {' + (($environmentMethods | ForEach-Object ToFullString) -join "`n") + $placeholderProperty[0].ToFullString() + '} }'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($environmentSource, $parseOptions))
$resourceNames = @($trees | ForEach-Object { $_.GetRoot().DescendantNodes() } | Where-Object {
    $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax] -and $_.Expression.ToString() -eq 'StringResources'
} | ForEach-Object { $_.Name.Identifier.ValueText } | Sort-Object -Unique)
$properties = ($resourceNames | Where-Object { $_ -ne 'Get' } | ForEach-Object { 'public static string ' + $_ + ' => Get("' + $_ + '");' }) -join "`n"
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('namespace Harness.Helpers { public static partial class StringResources {' + $properties + '} }', $parseOptions))
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;', $parseOptions))
$referencePaths = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + @($corePath, $protectedDataPath)
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@($referencePaths | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('HarnessSettingsReliability_' + [Guid]::NewGuid().ToString('N'), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
$emit = $compilation.Emit($stream)
if (-not $emit.Success) {
    $emit.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { $_.ToString() }
    throw 'Settings reliability regression compilation failed.'
}
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$run = $assembly.GetType('SettingsReliabilityRegression').GetMethod('RunAsync').CreateDelegate([Func[Threading.Tasks.Task]])
$task = $run.Invoke()
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (-not $task.IsCompleted) {
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Settings reliability regression exceeded its 30-second deadline.' }
    Start-Sleep -Milliseconds 25
}
if ($task.IsFaulted) { throw $task.Exception.GetBaseException() }
if ($task.IsCanceled) { throw 'Settings reliability regression was cancelled.' }
