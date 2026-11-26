Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Add-Type -TypeDefinition 'internal sealed class TrayRegressionCompilerBootstrap { }'
$options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$sources = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($selection in @(
    @{ Path = 'src/Harness/Services/TrayIconService.cs'; Type = 'namespace Harness.Services { internal sealed partial class TrayIconService {'; Methods = @('TryAddIcon', 'OnWindowMessage', 'EnsureAvailable', 'UpdateStatus') },
    @{ Path = 'src/Harness/MainWindow.Tray.cs'; Type = 'namespace Harness { internal sealed partial class MainWindow {'; Methods = @('OnTrayAvailabilityLost', 'HideMainWindowToTray', 'UpdateTrayStatus') },
    @{ Path = 'src/Harness/MainWindow.Lifecycle.cs'; Type = 'namespace Harness { internal sealed partial class MainWindow {'; Methods = @('OnAppWindowClosing') }
)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $selection.Path) -Raw), $options)
    $methods = @($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $selection.Methods })
    if ($methods.Count -ne $selection.Methods.Count) { throw "Missing tray recovery methods in $($selection.Path)" }
    $source = 'using System.Runtime.InteropServices; using Harness.Services; using Harness.Helpers; ' + $selection.Type + (($methods | ForEach-Object { $_.ToFullString() }) -join "`n") + '} }'
    $sources.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $options))
}
$sources.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'tools/TrayRecoveryRegression.cs') -Raw), $options))
$sources.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness.Core/Services/TrayClosePolicy.cs') -Raw), $options))
$sources.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('global using System; global using System.Linq; global using System.Collections.Generic;', $options))
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$compilationOptions = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('TrayRecovery_' + [Guid]::NewGuid().ToString('N'), $sources, $references, $compilationOptions)
$stream = [IO.MemoryStream]::new()
$emit = $compilation.Emit($stream)
if (!$emit.Success) {
    $emit.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { $_.ToString() }
    throw 'Tray recovery regression compilation failed.'
}
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$run = $assembly.GetType('TrayRecoveryRegression').GetMethod('Run').CreateDelegate([Action])
try { $run.Invoke() }
catch { throw $_.Exception.GetBaseException() }
