Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePath = Join-Path $repoRoot 'src/Harness/Services/WebViewService.OpenClaw.cs'
$source = Get-Content -LiteralPath $sourcePath -Raw
$match = [regex]::Match($source, '(?s)private const string OpenClawSelectionScript = """\s*(.*?)\s*""";')
if (-not $match.Success) {
    throw 'Could not locate the authored OpenClaw selection script.'
}

$nodeCommand = $env:OPENCLAW_NODE
if ([string]::IsNullOrWhiteSpace($nodeCommand)) {
    $resolved = Get-Command node -ErrorAction SilentlyContinue
    if ($resolved) { $nodeCommand = $resolved.Source }
}
if ([string]::IsNullOrWhiteSpace($nodeCommand)) {
    throw 'Node.js is required. Set OPENCLAW_NODE to the Node executable.'
}

$scriptLiteral = $match.Groups[1].Value | ConvertTo-Json -Compress
$runner = @'
import assert from 'node:assert/strict';
import vm from 'node:vm';

const selectionScript = __SELECTION_SCRIPT__;
let checks = 0;
function read({ app = null, editors = [], files = [], ready = 'complete', href = 'https://gateway.example/chat/owned-session' } = {}) {
  const document = {
    readyState: ready,
    querySelector: selector => selector === 'openclaw-app' ? app : null,
    querySelectorAll: selector => selector === 'input[type="file"]' ? files : editors,
  };
  const result = vm.runInNewContext(selectionScript, { document, location: Object.freeze({ href }) }, { timeout: 1000 });
  return result == null ? result : JSON.parse(JSON.stringify(result));
}
function check(name, action) { action(); checks++; console.log(`PASS: ${name}`); }
const connected = { connected: true, sessionKey: 'agent:main:chat-1', chatRunId: 'run-owned', chatMessage: '' };

check('missing page and no editor is unavailable', () => assert.equal(read(), null));
check('legacy page must explicitly report connected', () => {
  for (const connected of [false, undefined, 'true', 1]) {
    assert.equal(read({ app: { connected, sessionKey: 'a' }, editors: [{ value: '' }] }), null);
  }
});
check('malformed legacy keys do not establish selection', () => {
  for (const sessionKey of [null, undefined, 17, {}]) {
    assert.equal(read({ app: { connected: true, sessionKey } }), null);
  }
});
check('connected legacy selection preserves exact key and run identity', () => {
  assert.deepEqual(read({ app: connected }), {
    sessionKey: 'agent:main:chat-1', runId: 'run-owned', hasDraft: false, legacy: true,
    href: 'https://gateway.example/chat/owned-session',
  });
});
check('unknown run identity stays unknown', () => {
  for (const chatRunId of [null, undefined, 7, {}]) {
    assert.equal(read({ app: { ...connected, chatRunId } }).runId, null);
  }
});
check('a newer run is reported without retaining previous selection', () => {
  assert.equal(read({ app: { ...connected, chatRunId: 'run-next' } }).runId, 'run-next');
  assert.equal(read({ app: { ...connected, chatRunId: null } }).runId, null);
});
check('legacy text drafts block navigation', () => {
  assert.equal(read({ app: { ...connected, chatMessage: ' unsent ' } }).hasDraft, true);
  assert.equal(read({ app: { ...connected, chatMessage: ' \n ' } }).hasDraft, false);
});
check('attachment-only drafts block navigation', () => {
  assert.equal(read({ app: { ...connected, chatAttachments: [{ id: 'upload-1' }] } }).hasDraft, true);
  assert.equal(read({ app: connected, files: [{ files: [{ name: 'draft.txt' }] }] }).hasDraft, true);
});
check('textarea and contenteditable drafts are retained', () => {
  assert.equal(read({ app: connected, editors: [{ value: 'pending text' }] }).hasDraft, true);
  assert.equal(read({ app: connected, editors: [{ textContent: 'pending rich text' }] }).hasDraft, true);
  assert.equal(read({ app: connected, editors: [{ value: '  ' }, { textContent: '' }] }).hasDraft, false);
});
check('modern route requires loaded page and composer', () => {
  assert.equal(read({ ready: 'loading', editors: [{ value: '' }] }), null);
  assert.equal(read({ ready: 'complete', editors: [] }), null);
});
check('modern page reports URL for native route validation without inventing key or run', () => {
  const href = 'https://gateway.example/base/chat/session%2Fkey';
  assert.deepEqual(read({ editors: [{ value: '' }], href }), {
    sessionKey: '', runId: null, hasDraft: false, legacy: false, href,
  });
});
check('modern unsent text blocks route switching', () => {
  assert.equal(read({ editors: [{ textContent: 'keep my draft' }] }).hasDraft, true);
});
check('selection probe is read-only and does not access credentials', () => {
  const app = Object.freeze({ ...connected });
  const forbidden = () => { throw new Error('Credential access is forbidden'); };
  const document = Object.freeze({
    readyState: 'complete',
    querySelector: () => app,
    querySelectorAll: () => [],
    get cookie() { return forbidden(); },
  });
  const result = vm.runInNewContext(selectionScript, {
    document,
    location: Object.freeze({ href: 'https://gateway.example/' }),
    localStorage: new Proxy({}, { get: forbidden }),
    sessionStorage: new Proxy({}, { get: forbidden }),
    fetch: forbidden,
  }, { timeout: 1000 });
  assert.equal(result.runId, 'run-owned');
  assert.deepEqual(app, connected);
});
console.log(`OpenClaw selection behavior: ${checks} checks passed.`);
'@
$runner = $runner.Replace('__SELECTION_SCRIPT__', $scriptLiteral)
$tempFile = Join-Path ([IO.Path]::GetTempPath()) ('harness-selection-' + [guid]::NewGuid().ToString('N') + '.mjs')
try {
    [IO.File]::WriteAllText($tempFile, $runner, [Text.UTF8Encoding]::new($false))
    & $nodeCommand $tempFile
    if ($LASTEXITCODE -ne 0) { throw "OpenClaw selection verification failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
}
