param([string]$OutputDirectory = 'artifacts/update-presentation-regression')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Add-Type -TypeDefinition 'internal sealed class UpdatePresentationCompilerBootstrap { }'
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($path in @(
    'src/Harness/ViewModels/UpdateViewModel.cs',
    'src/Harness/Helpers/UpdateLocalization.cs',
    'src/Harness/Helpers/SimpleCommand.cs',
    'src/Harness.Core/Services/IAppLogger.cs',
    'tools/UpdatePresentationRegression.cs'
)) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), $parseOptions))
}
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('global using System; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;', $parseOptions))
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('UpdatePresentationRegression_' + [Guid]::NewGuid().ToString('N'), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
$emit = $compilation.Emit($stream)
if (-not $emit.Success) {
    $emit.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { $_.ToString() }
    throw 'Update presentation regression compilation failed.'
}
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$output = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
[IO.Directory]::CreateDirectory($output) | Out-Null
$run = $assembly.GetType('UpdatePresentationRegression').GetMethod('Run').CreateDelegate([Action[string,string]])
try { $run.Invoke($repoRoot, $output) }
catch { throw $_.Exception.GetBaseException() }
