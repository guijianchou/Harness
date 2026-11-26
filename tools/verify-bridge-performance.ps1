param(
  [string]$OutputDirectory = 'artifacts/bridge-performance',
  [string]$BaselineAssemblyPath
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'Use pwsh -STA.' }
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$Current = [string]::IsNullOrWhiteSpace($BaselineAssemblyPath)
$lock = Get-Content (Join-Path $RepoRoot 'src/Harness/packages.lock.json') -Raw | ConvertFrom-Json
$version = @($lock.dependencies.PSObject.Properties | ForEach-Object { $_.Value.PSObject.Properties['Microsoft.Web.WebView2'].Value.resolved } | Where-Object { $_ })[0]
$sdk = Join-Path $env:USERPROFILE ".nuget/packages/microsoft.web.webview2/$version"
$core = Join-Path $sdk 'lib/net462/Microsoft.Web.WebView2.Core.dll'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path $core
$scripts = @{}
if($Current) {
  Get-ChildItem (Join-Path $RepoRoot 'src/Harness/Services') -Filter 'HostedUiBridge*.js' | ForEach-Object { $scripts[$_.Name] = [IO.File]::ReadAllText($_.FullName) }
} else {
  $preview = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($BaselineAssemblyPath))
  foreach ($name in $preview.GetManifestResourceNames() | Where-Object { $_ -like '*HostedUiBridge*.js' }) {
    $reader = [IO.StreamReader]::new($preview.GetManifestResourceStream($name))
    $scripts[$name.Replace('Harness.Services.','')] = $reader.ReadToEnd()
    $reader.Dispose()
  }
}
$bridge = $scripts['HostedUiBridge.Script.js']
$bridge = $bridge.Replace('__OPENCLAW_BRIDGE_STRINGS_JSON__', '{}').Replace('__OPENCLAW_OWNER_TOKEN_JSON__', '"synthetic"').Replace('__HARNESS_BACKEND_PROFILE_JSON__', '{"kind":"OpenClaw","appStateElement":"openclaw-app","commandGlobals":[]}')
$parts = @{
 '__HARNESS_LABEL_VOCABULARY_SCRIPT__'='LabelVocabulary'; '__OPENCLAW_HOST_MESSAGING_SCRIPT__'='HostMessaging'; '__OPENCLAW_MUTATION_FILTER_SCRIPT__'='MutationFilter';
 '__OPENCLAW_MODEL_RESOLVER_SCRIPT__'='ModelResolver'; '__OPENCLAW_DOM_UTILITIES_SCRIPT__'='DomUtilities'; '__OPENCLAW_MODEL_DOM_FALLBACK_SCRIPT__'='ModelDomFallback';
 '__HARNESS_BACKEND_ADAPTER_SCRIPT__'='BackendAdapter';
 '__OPENCLAW_ACTIVITY_STATE_SCRIPT__'='ActivityState'; '__OPENCLAW_PHASE_CLASSIFIER_SCRIPT__'='PhaseClassifier'; '__OPENCLAW_STATUS_INSPECTION_SCRIPT__'='StatusInspection'; '__OPENCLAW_COMMAND_DISPATCH_SCRIPT__'='CommandDispatch'
}
foreach($entry in $parts.GetEnumerator()) { $bridge = $bridge.Replace($entry.Key, $scripts['HostedUiBridge.'+$entry.Value+'.js']) }
$bridge = $bridge.Replace('      inspectControlUi,', '      inspectControlUi: () => { const start=performance.now(); const result=inspectControlUi(); window.measurements.inspectCount++; window.measurements.inspectMs+=performance.now()-start; return result; },')
$bridge = $bridge.Replace('window.chrome.webview.postMessage(attachOwnership(message));', '(window.statusMessages ??= []).push(message); window.chrome.webview.postMessage(attachOwnership(message));')
$label=if($Current){'current'}else{'baseline'}
[IO.File]::WriteAllText((Join-Path $output ($label+'-bridge.js')),$bridge)
$source = Get-Content (Join-Path $PSScriptRoot 'BridgePerformance.cs') -Raw
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BridgePerformance.js') -Destination (Join-Path $output 'measure.js')
$references = @((Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + @($core)
foreach($name in @('System.Windows.Forms.dll','System.Windows.Forms.Primitives.dll','System.Drawing.Common.dll','System.Private.Windows.Core.dll')) { $references += Join-Path $PSHOME $name }
$metadata = [Microsoft.CodeAnalysis.MetadataReference[]]@($references | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('HarnessPerformance_'+[Guid]::NewGuid().ToString('N'), [Microsoft.CodeAnalysis.SyntaxTree[]]@($tree), $metadata,$options)
$stream=[IO.MemoryStream]::new(); $emit=$compilation.Emit($stream)
if(!$emit.Success) { $emit.Diagnostics | ForEach-Object { $_.ToString() }; throw 'Compilation failed' }
$assembly=[Reflection.Assembly]::Load($stream.ToArray())
$run=$assembly.GetType('HarnessPerformance').GetMethod('Run').CreateDelegate([Action[string,string,string,bool]])
$run.Invoke((Join-Path $sdk 'runtimes/win-x64/native'),$output,$label,$Current)
