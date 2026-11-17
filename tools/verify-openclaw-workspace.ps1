param([string]$OutputDirectory = 'artifacts/openclaw-workspace-regression')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Add-Type -TypeDefinition 'internal sealed class OpenClawRegressionCompilerBootstrap { }'
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$sourceFiles = @((Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src/Harness.Core/OpenClaw') -Filter '*.cs').FullName)
$sourceFiles += @(
    'src/Harness/Services/OpenClawSessionRuntime.cs',
    'src/Harness/ViewModels/MainViewModel.OpenClaw.Properties.cs',
    'src/Harness/ViewModels/MainViewModel.OpenClaw.Commands.cs',
    'src/Harness/ViewModels/MainViewModel.OpenClaw.Data.cs',
    'src/Harness/ViewModels/MainViewModel.OpenClaw.SessionDetails.cs',
    'src/Harness/ViewModels/RunIndicatorMode.cs',
    'src/Harness/Helpers/GatewayStrings.cs',
    'src/Harness/Helpers/SimpleCommand.cs',
    'src/Harness.Core/Services/NativeNotificationRequest.cs',
    'tools/OpenClawWorkspaceRegression.cs'
) | ForEach-Object { Join-Path $repoRoot $_ }
foreach ($path in $sourceFiles) {
    $source = Get-Content -LiteralPath $path -Raw
    # Roslyn's in-process compilation has no SDK generators; keep the same regex behavior.
    $source = $source.Replace('[GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]', '')
    $source = $source.Replace('private static partial Regex CanonicalAgent();', 'private static Regex CanonicalAgent() => new Regex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);')
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $parseOptions))
}
$lifecycle = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/ViewModels/MainViewModel.OpenClaw.Lifecycle.cs') -Raw), $parseOptions)
$currentMethod = $lifecycle.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsCurrentGateway' } | Select-Object -First 1
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('using Harness.Services; namespace Harness.ViewModels { public partial class MainViewModel {' + $currentMethod.ToFullString() + '} }', $parseOptions))
$shellProperties = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/ViewModels/MainViewModel.Core.Properties.cs') -Raw), $parseOptions)
$stopProperties = @($shellProperties.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -in @('CanStop', 'IsLoading', 'LoadingVisibility', 'IsPlaceholderEnvironment') } | ForEach-Object { $_.ToFullString() })
if ($stopProperties.Count -ne 4) { throw 'Missing authored Stop availability properties.' }
$shellCommands = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/ViewModels/MainViewModel.Commands.cs') -Raw), $parseOptions)
$stopMethod = $shellCommands.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'OnStopAsync' } | Select-Object -First 1
if ($null -eq $stopMethod) { throw 'Missing authored toolbar Stop handler.' }
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('using Microsoft.UI.Xaml; namespace Harness.ViewModels { public partial class MainViewModel {' + ($stopProperties -join "`n") + $stopMethod.ToFullString() + '} }', $parseOptions))
$pageTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'src/Harness/Services/WebViewService.OpenClaw.cs') -Raw), $parseOptions)
$pageRecord = $pageTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax] -and $_.Identifier.ValueText -eq 'OpenClawPageSelection' } | Select-Object -First 1
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('namespace Harness.Services {' + $pageRecord.ToFullString() + '}', $parseOptions))
# Reuse the existing real loopback WebSocket fixture without duplicating its transport implementation.
$fixtureTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot 'tests/Harness.Core.Tests/OpenClawGatewayClientTests.cs') -Raw), $parseOptions)
$fixtureClasses = @($fixtureTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('FakeGateway', 'TestIdentity') } | ForEach-Object { $_.ToFullString().Replace('private sealed class', 'internal sealed class') })
$fixtureSource = 'using System.Net; using System.Net.Sockets; using System.Net.WebSockets; using System.Security.Cryptography; using System.Text; using System.Text.Json; using Harness.OpenClaw; internal static class GatewayFixtures {' + ($fixtureClasses -join "`n") + '}'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($fixtureSource, $parseOptions))
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('global using System; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;', $parseOptions))
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('OpenClawWorkspaceRegression_' + [Guid]::NewGuid().ToString('N'), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
$emit = $compilation.Emit($stream)
if (!$emit.Success) {
    $emit.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { $_.ToString() }
    throw 'OpenClaw workspace regression compilation failed.'
}
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
[IO.Directory]::CreateDirectory($output) | Out-Null
$run = $assembly.GetType('OpenClawWorkspaceRegression').GetMethod('Run').CreateDelegate([Action[string,string]])
try { $run.Invoke($repoRoot, $output) }
catch { throw $_.Exception.GetBaseException() }
