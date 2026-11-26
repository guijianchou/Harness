Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$servicesRoot = Join-Path $repoRoot 'src/Harness/Services'
$hostMessagingPath = Join-Path $servicesRoot 'HostedUiBridge.HostMessaging.js'
$mutationFilterPath = Join-Path $servicesRoot 'HostedUiBridge.MutationFilter.js'
$resolverPath = Join-Path $servicesRoot 'HostedUiBridge.ModelResolver.js'
$domUtilitiesPath = Join-Path $servicesRoot 'HostedUiBridge.DomUtilities.js'
$backendAdapterPath = Join-Path $servicesRoot 'HostedUiBridge.BackendAdapter.js'
$modelDomFallbackPath = Join-Path $servicesRoot 'HostedUiBridge.ModelDomFallback.js'
$activityStatePath = Join-Path $servicesRoot 'HostedUiBridge.ActivityState.js'
$phaseClassifierPath = Join-Path $servicesRoot 'HostedUiBridge.PhaseClassifier.js'
$statusInspectionPath = Join-Path $servicesRoot 'HostedUiBridge.StatusInspection.js'
$commandDispatchPath = Join-Path $servicesRoot 'HostedUiBridge.CommandDispatch.js'
$labelVocabularyPath = Join-Path $servicesRoot 'HostedUiBridge.LabelVocabulary.js'
$bridgeScriptPath = Join-Path $servicesRoot 'HostedUiBridge.Script.js'

function Read-Utf8Script {
    param([Parameter(Mandatory = $true)][string]$Path)

    # Read a plain string so Windows PowerShell does not serialize provider
    # metadata along with script text when passing it to ConvertTo-Json.
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

$bridgeScriptTemplate = Read-Utf8Script $bridgeScriptPath
if ($bridgeScriptTemplate -notmatch 'nextPollAt' -or $bridgeScriptTemplate -notmatch 'scheduleNextPoll') {
    throw 'Bridge polling must use drift-aware scheduling.'
}
Write-Host 'PASS: polling uses nextPollAt drift correction'

if ($bridgeScriptTemplate -notmatch 'getPollInterval' -or $bridgeScriptTemplate -notmatch 'getPollInterval\(snapshot\)') {
    throw 'Bridge polling interval must be snapshot-driven.'
}
Write-Host 'PASS: polling interval is snapshot-driven'

if ($bridgeScriptTemplate -notmatch 'const postSessionReady' -or
    $bridgeScriptTemplate -notmatch "snapshot\.phase !== 'connected'" -or
    $bridgeScriptTemplate -notmatch 'reportSessionReady:\s*\(\) => \{[\s\S]*return postSessionReady\(\)') {
    throw 'Bridge session-ready replay must wait for connected shell state and be callable by native code.'
}
Write-Host 'PASS: session-ready replay is native-callable'

function Resolve-NodeCommand {
    if (-not [string]::IsNullOrWhiteSpace($env:OPENCLAW_NODE)) {
        return $env:OPENCLAW_NODE
    }

    $command = Get-Command node -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

$nodeCommand = Resolve-NodeCommand
if (-not $nodeCommand) {
    if ($env:OPENCLAW_ALLOW_NODE_SKIP -eq '1') {
        Write-Host 'SKIP: node is not available and OPENCLAW_ALLOW_NODE_SKIP=1; bridge script verification skipped.'
        exit 0
    }

    throw 'Node.js is required for bridge script verification. Set OPENCLAW_NODE to a Node executable, or set OPENCLAW_ALLOW_NODE_SKIP=1 only for an explicit local skip.'
}

try {
    & $nodeCommand --version | Out-Null
} catch {
    if ($env:OPENCLAW_ALLOW_NODE_SKIP -eq '1') {
        Write-Host "SKIP: node is not executable and OPENCLAW_ALLOW_NODE_SKIP=1; bridge script verification skipped. $($_.Exception.Message)"
        exit 0
    }

    throw "Node.js is required for bridge script verification, but '$nodeCommand' is not executable. Set OPENCLAW_NODE to a working Node executable, or set OPENCLAW_ALLOW_NODE_SKIP=1 only for an explicit local skip. $($_.Exception.Message)"
}

function Invoke-NodeRunner {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $tempFile = Join-Path ([System.IO.Path]::GetTempPath()) ('harness-' + $Name + '-' + [System.Guid]::NewGuid() + '.mjs')
    try {
        Set-Content -LiteralPath $tempFile -Value $Source -Encoding UTF8
        & $nodeCommand $tempFile
        if ($LASTEXITCODE -ne 0) {
            throw "Bridge script verification failed in $Name with exit code $LASTEXITCODE."
        }
    } finally {
        Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
    }
}

$bridgeStrings = @{
    bridgeGatewayUiLoaded = 'loaded'
    bridgePageLoading = 'loading'
    bridgeConnectedSummary = 'connected summary'
} | ConvertTo-Json -Compress

$assembledBridgeScript = $bridgeScriptTemplate
$backendProfileJson = @{
    kind = 'OpenClaw'
    appStateElement = 'openclaw-app'
    hostCommandEventPrefix = 'openclaw'
    commandGlobals = @('__openclaw', '__OPENCLAW__', '__APP__', 'app')
} | ConvertTo-Json -Compress

$scriptReplacements = [ordered]@{
    '__OPENCLAW_BRIDGE_STRINGS_JSON__' = $bridgeStrings
    '__OPENCLAW_OWNER_TOKEN_JSON__' = '"owner-token-test"'
    '__HARNESS_BACKEND_PROFILE_JSON__' = $backendProfileJson
    '__HARNESS_LABEL_VOCABULARY_SCRIPT__' = Read-Utf8Script $labelVocabularyPath
    '__OPENCLAW_HOST_MESSAGING_SCRIPT__' = Read-Utf8Script $hostMessagingPath
    '__OPENCLAW_MUTATION_FILTER_SCRIPT__' = Read-Utf8Script $mutationFilterPath
    '__OPENCLAW_MODEL_RESOLVER_SCRIPT__' = Read-Utf8Script $resolverPath
    '__OPENCLAW_DOM_UTILITIES_SCRIPT__' = Read-Utf8Script $domUtilitiesPath
    '__HARNESS_BACKEND_ADAPTER_SCRIPT__' = Read-Utf8Script $backendAdapterPath
    '__OPENCLAW_MODEL_DOM_FALLBACK_SCRIPT__' = Read-Utf8Script $modelDomFallbackPath
    '__OPENCLAW_ACTIVITY_STATE_SCRIPT__' = Read-Utf8Script $activityStatePath
    '__OPENCLAW_PHASE_CLASSIFIER_SCRIPT__' = Read-Utf8Script $phaseClassifierPath
    '__OPENCLAW_STATUS_INSPECTION_SCRIPT__' = Read-Utf8Script $statusInspectionPath
    '__OPENCLAW_COMMAND_DISPATCH_SCRIPT__' = Read-Utf8Script $commandDispatchPath
}

foreach ($entry in $scriptReplacements.GetEnumerator()) {
    $assembledBridgeScript = $assembledBridgeScript.Replace($entry.Key, $entry.Value)
}

$assembledBridgeScriptLiteral = $assembledBridgeScript | ConvertTo-Json -Compress

$runner = @(
    Read-Utf8Script $labelVocabularyPath
    Read-Utf8Script $hostMessagingPath
    Read-Utf8Script $mutationFilterPath
    Read-Utf8Script $resolverPath
    Read-Utf8Script $domUtilitiesPath
    Read-Utf8Script $backendAdapterPath
    Read-Utf8Script $modelDomFallbackPath
    Read-Utf8Script $activityStatePath
    Read-Utf8Script $phaseClassifierPath
    Read-Utf8Script $statusInspectionPath
    Read-Utf8Script $commandDispatchPath
) -join "`n"

$asyncExecutionLiteral = Read-Utf8Script (Join-Path $servicesRoot 'WebViewCommands.ExecuteAsync.js') | ConvertTo-Json -Compress
$runner += "`nconst asyncExecutionTemplate = $asyncExecutionLiteral;`n"
$stopInjectionSource = Read-Utf8Script (Join-Path $servicesRoot 'WebViewCommands.StopInjection.js')
$abortRunLiteral = (Read-Utf8Script (Join-Path $servicesRoot 'WebViewCommands.AbortRun.js')).Replace('__HARNESS_STOP_INJECTION_SCRIPT__', $stopInjectionSource) | ConvertTo-Json -Compress
$stopInjectionLiteral = $stopInjectionSource | ConvertTo-Json -Compress
$runner += "`nconst abortRunScript = $abortRunLiteral;`nconst stopInjectionScript = $stopInjectionLiteral;`n"

$runner += @'

let failed = 0;

const assertEqual = (name, actual, expected) => {
  if (actual !== expected) {
    console.error(`FAIL: ${name}: expected ${expected}, got ${actual}`);
    failed += 1;
    return;
  }

  console.log(`PASS: ${name}`);
};

const assertTrue = (name, condition, detail = '') => {
  if (!condition) {
    console.error(`FAIL: ${name}${detail ? `: ${detail}` : ''}`);
    failed += 1;
    return;
  }

  console.log(`PASS: ${name}`);
};

const modelCases = [
  {
    name: 'MODEL default model',
    states: [{
      sessionsResult: {
        defaults: { model: 'gpt-5.5', modelProvider: 'openai' },
        sessions: []
      }
    }],
    sessionKey: 's1',
    expected: 'openai/gpt-5.5'
  },
  {
    name: 'MODEL null override session precedence',
    states: [{
      sessionKey: 's1',
      chatModelOverrides: { s1: null },
      sessionsResult: {
        defaults: { model: 'gpt-5.4', modelProvider: 'openai' },
        sessions: [{ key: 's1', model: 'claude-sonnet-4.5', modelProvider: 'anthropic' }]
      }
    }],
    sessionKey: 's1',
    expected: 'anthropic/claude-sonnet-4.5'
  },
  {
    name: 'MODEL object override',
    states: [{
      chatModelOverrides: {
        s1: {
          model: { id: 'qwen3-coder' },
          provider: { id: 'dashscope' }
        }
      },
      sessionsResult: {
        defaults: { model: 'gpt-5.4', modelProvider: 'openai' },
        sessions: []
      }
    }],
    sessionKey: 's1',
    expected: 'dashscope/qwen3-coder'
  }
];

for (const item of modelCases) {
  const result = resolveOpenClawAppStateModel(item.states, item.sessionKey);
  assertEqual(item.name, result && result.value, item.expected);
}

assertEqual('DOM utilities compact whitespace', openClawDomUtilities.compactText('  a \n\t b  '), 'a b');

const fakeMutationFilter = { isStatusProbeExcludedElement: () => false };
const modelDomReader = openClawModelDomFallback.createReader({
  dom: openClawDomUtilities,
  mutationFilter: fakeMutationFilter,
  modelResolver: openClawModelResolver
});
assertEqual('MODEL DOM fallback sanitizes control labels',
  modelDomReader.sanitizeModelLabel('Current model: openai/gpt-5.5'),
  'openai/gpt-5.5');
assertEqual('MODEL DOM fallback collapses duplicated model labels',
  modelDomReader.sanitizeModelLabel('MODEL: glm-5.2-glm-5.2'),
  'glm-5.2');
assertEqual('MODEL DOM fallback preserves provider while collapsing duplicate',
  modelDomReader.sanitizeModelLabel('MODEL: openclaw/glm-5.2-glm-5.2'),
  'openclaw/glm-5.2');

const makeVisibleElement = ({ text = '', ariaLabel = '', title = '', child = null } = {}) => ({
  innerText: text,
  textContent: text,
  getAttribute: (name) => name === 'aria-label' ? ariaLabel : name === 'title' ? title : null,
  getBoundingClientRect: () => ({ width: 100, height: 20, top: 40 }),
  querySelector: () => child
});
globalThis.HTMLSelectElement = class HTMLSelectElement {};
const runModelDomFixture = (kind) => {
  const modelLabel = makeVisibleElement({
    text: kind === 'openclaw' ? 'glm-5.2' : 'DeepSeek-V4-Flash'
  });
  const effortLabel = makeVisibleElement({ text: 'High' });
  const modelButton = makeVisibleElement({
    ariaLabel: 'Select model, current DeepSeek-V4-Flash, reasoning effort High',
    title: 'DeepSeek-V4-Flash \u00b7 High',
    child: modelLabel
  });
  globalThis.window = { getComputedStyle: () => ({ display: 'block', visibility: 'visible' }) };
  globalThis.document = {
    querySelector: () => null,
    querySelectorAll: (selector) => {
      if (kind === 'openclaw' && selector === 'summary[data-model-select="true"] .chat-controls__inline-select-label, .chat-controls__model-picker .chat-controls__inline-select-label') {
        return [modelLabel];
      }
      if (kind === 'openclaw' && selector === '.chat-controls__inline-select-label') {
        return [modelLabel, effortLabel];
      }
      if (kind === 'deepseek' && selector === 'button[aria-label], [role="button"][aria-label]') {
        return [modelButton, makeVisibleElement({ ariaLabel: 'Settings' })];
      }
      if (kind === 'deepseek' && selector === 'button[aria-label*="reasoning effort"], button[aria-label*="Reasoning effort"]') {
        return [modelButton];
      }
      return [];
    }
  };

  const reader = openClawModelDomFallback.createReader({
    dom: openClawDomUtilities,
    mutationFilter: fakeMutationFilter,
    modelResolver: openClawModelResolver,
    profile: { kind: kind === 'openclaw' ? 'OpenClaw' : 'Generic' }
  });
  return { model: reader.readCurrentModel(), effort: reader.readCurrentEffort() };
};
const openClawFixture = runModelDomFixture('openclaw');
assertEqual('MODEL DOM fixture reads OpenClaw model instead of effort',
  openClawFixture.model.value,
  'glm-5.2');
assertEqual('MODEL DOM fixture identifies OpenClaw model source',
  openClawFixture.model.source,
  'dom:openclaw-model-label');
const deepSeekFixture = runModelDomFixture('deepseek');
assertEqual('ordinary DeepSeek web page does not expose agent model telemetry',
  deepSeekFixture.model.value,
  '');
assertEqual('ordinary DeepSeek web page does not expose agent effort telemetry',
  deepSeekFixture.effort.value,
  '');

globalThis.window = {};
globalThis.document = { querySelector: () => null, querySelectorAll: () => [] };
const activityTracker = openClawActivityState.createActivityTracker({
  dom: openClawDomUtilities,
  mutationFilter: fakeMutationFilter
});
const idleSnapshot = activityTracker.applyBusyStaleness({ phase: 'connected', workState: 'idle', isBusy: false });
assertTrue('activity state marks idle snapshot non-stale',
  idleSnapshot.isBusyStale === false && idleSnapshot.busyStaleSeconds === 0);

const realDateNow = Date.now;
let activityNow = 1000;
Date.now = () => activityNow;
const conversation = Array.from({ length: 5 }, (_, index) => ({ id: `message-${index}`, text: `text-${index}` }));
globalThis.document = { querySelectorAll: () => conversation };
const genericActivity = openClawActivityState.createActivityTracker({
  dom: { isVisible: () => true, textOf: (element) => element.text, compactText: (text) => String(text || '') },
  mutationFilter: fakeMutationFilter,
  profile: {}
});
const inspectActivity = () => genericActivity.applyBusyStaleness({
  phase: 'connected', workState: 'busy', isBusy: true,
  activitySignature: genericActivity.collectDomActivitySignature()
});
inspectActivity();
activityNow += 31000;
conversation[4].text = 'newest';
assertTrue('generic activity observes output after the fourth message', inspectActivity().isBusyStale === false);
activityNow += 31000;
conversation[4].text = 'update';
assertTrue('generic activity observes same-length streaming edits', inspectActivity().isBusyStale === false);
activityNow += 31000;
assertTrue('generic activity still identifies a stalled stream', inspectActivity().isBusyStale === true);
Date.now = realDateNow;

const phaseStrings = {
  bridgeGatewayUiLoaded: 'loaded',
  bridgePageLoading: 'loading',
  bridgeTokenMissingSummary: 'token missing summary',
  bridgeTokenMissingDetail: 'token missing detail',
  bridgeTokenMismatchSummary: 'token mismatch summary',
  bridgeTokenMismatchDetail: 'token mismatch detail',
  bridgeDeviceTokenMismatchSummary: 'device mismatch summary',
  bridgeDeviceTokenMismatchDetail: 'device mismatch detail',
  bridgeOriginRejectedSummary: 'origin summary',
  bridgeOriginRejectedDetail: 'origin detail',
  bridgeTrustedProxyLoopbackSummary: 'loopback summary',
  bridgeTrustedProxyLoopbackDetail: 'loopback detail',
  bridgeMixedAuthSummary: 'mixed summary',
  bridgeMixedAuthDetail: 'mixed detail',
  bridgeTrustedProxyHeaderSummary: 'header summary',
  bridgeTrustedProxyHeaderDetail: 'header detail',
  bridgeTrustedProxyOriginSummary: 'proxy origin summary',
  bridgeTrustedProxyOriginDetail: 'proxy origin detail',
  bridgeRateLimitedSummary: 'rate summary',
  bridgeRateLimitedDetail: 'rate detail',
  bridgeInsecureHttpSummary: 'http summary',
  bridgeInsecureHttpDetail: 'http detail',
  bridgePairingSummary: 'pairing summary',
  bridgePairingDetail: 'pairing detail',
  bridgeAuthRequiredSummary: 'auth summary',
  bridgeAuthRequiredDetail: 'auth detail',
  bridgeGatewaySessionNotConnectedSummary: 'gateway summary',
  bridgeGatewaySessionNotConnectedDetail: 'gateway detail',
  bridgeConnectingSummary: 'connecting summary',
  bridgeConnectingDetail: 'connecting detail',
  bridgeConnectedSummary: 'connected summary'
};
const authPhase = openClawPhaseClassifier.classify({
  documentReadyState: 'complete',
  hasBody: true,
  text: 'auth_token_missing',
  lowerUrl: 'https://example.test/',
  strings: phaseStrings,
  shellDetected: false
});
assertTrue('phase classifier maps token missing to auth_required',
  authPhase.phase === 'auth_required' && authPhase.summary === 'token missing summary');

globalThis.HTMLSelectElement = class HTMLSelectElement {};
globalThis.HTMLInputElement = class HTMLInputElement {};
globalThis.HTMLTextAreaElement = class HTMLTextAreaElement {};
const appElement = {
  connected: true,
  tab: 'chat',
  chatMessages: [],
  sessionsResult: {
    defaults: { model: 'gpt-5.5', modelProvider: 'openai' },
    sessions: []
  }
};
globalThis.window = {
  location: { href: 'https://example.test/?session=s1' }
};
globalThis.document = {
  readyState: 'complete',
  body: {},
  activeElement: null,
  querySelector: (selector) => selector === 'openclaw-app' ? appElement : null,
  querySelectorAll: () => []
};
const statusInspector = openClawStatusInspection.createInspector({
  strings: phaseStrings,
  mutationFilter: fakeMutationFilter,
  modelResolver: openClawModelResolver,
  statusKind: 'harness-control-ui-status',
  backendProfile: { kind: 'OpenClaw', appStateElement: 'openclaw-app', commandGlobals: ['__openclaw', '__OPENCLAW__', '__APP__', 'app'] }
});
const statusSnapshot = statusInspector.inspectControlUi();
assertTrue('status inspection composes split bridge modules',
  statusSnapshot.phase === 'connected' &&
  statusSnapshot.currentModel === 'openai/gpt-5.5' &&
  statusSnapshot.currentModelSource === 'app-state:default');

appElement.chatLoading = false;
appElement.configSaving = true;
appElement.cronBusy = true;
const shellBusySnapshot = statusInspector.inspectControlUi();
assertTrue('status inspection keeps settings/cron busy out of stale-chat recovery',
  shellBusySnapshot.isBusy === true &&
  shellBusySnapshot.isBusyStaleCandidate === false &&
  shellBusySnapshot.isBusyStale === false);

appElement.configSaving = false;
appElement.cronBusy = false;
appElement.chatLoading = true;
appElement.chatMessages = [{ id: 'm1', text: 'hello' }];
const chatBusySnapshot = statusInspector.inspectControlUi();
assertTrue('status inspection marks chat busy as stale-busy candidate',
  chatBusySnapshot.isBusy === true &&
  chatBusySnapshot.isBusyStaleCandidate === true &&
  chatBusySnapshot.activitySignature.includes('hello'));
appElement.chatLoading = false;

globalThis.CustomEvent = class CustomEvent {
  constructor(type, options) {
    this.type = type;
    this.detail = options?.detail;
  }
};

let invokedPayload = null;
let postedSnapshot = null;
let readySnapshot = null;
globalThis.window = {
  chat: {
    refreshSession: async (payload) => {
      invokedPayload = payload;
    }
  },
  dispatchEvent: () => false
};
globalThis.document = { dispatchEvent: () => false };

const openClawBackendProfile = {
  kind: 'OpenClaw',
  appStateElement: 'openclaw-app',
  hostCommandEventPrefix: 'openclaw',
  commandGlobals: ['__openclaw', '__OPENCLAW__', '__APP__', 'app']
};
const genericBackendProfile = {
  kind: 'Generic',
  appStateElement: null,
  hostCommandEventPrefix: null,
  commandGlobals: ['__APP__', 'app']
};

let commandHandler = openClawCommandDispatch.createCommandHandler({
  inspectControlUi: () => ({ phase: 'connected', shellDetected: true }),
  postStatus: (snapshot) => { postedSnapshot = snapshot; },
  checkSessionReady: (snapshot) => { readySnapshot = snapshot; },
  backendProfile: openClawBackendProfile
});

let handled = await commandHandler({ command: 'refresh_session', payload: { id: 42 } });
assertTrue('command dispatch returns handled true when bridge method exists',
  handled === true && invokedPayload?.id === 42 && postedSnapshot?.phase === 'connected' && readySnapshot?.shellDetected === true);

const dispatchedEvents = [];
globalThis.window = { dispatchEvent: (event) => { dispatchedEvents.push(event.type); return true; } };
globalThis.document = { dispatchEvent: (event) => { dispatchedEvents.push(event.type); return true; } };
commandHandler = openClawCommandDispatch.createCommandHandler({
  inspectControlUi: () => ({ phase: 'page_loaded', shellDetected: false }),
  postStatus: () => {},
  checkSessionReady: () => {},
  backendProfile: openClawBackendProfile
});
handled = await commandHandler({ command: 'refresh_session', payload: { fallback: true } });
assertTrue('command dispatch dispatches CustomEvent but returns unhandled when method missing',
  handled === false &&
  dispatchedEvents.includes('openclaw:host-command') &&
  dispatchedEvents.includes('openclaw:refresh_session'));

dispatchedEvents.length = 0;
handled = await commandHandler({ command: 'future_unknown_command', payload: { fallback: true } });
assertTrue('command dispatch returns unhandled for unknown CustomEvent fallback',
  handled === false &&
  dispatchedEvents.includes('openclaw:host-command') &&
  dispatchedEvents.includes('openclaw:future_unknown_command'));

globalThis.window = {};
assertEqual('host messaging returns false without chrome.webview', openClawHostMessaging.postHostMessage({ kind: 'test' }), false);

let postedHostMessage = null;
globalThis.window = {
  chrome: {
    webview: {
      postMessage: (message) => {
        postedHostMessage = message;
      }
    }
  }
};
openClawHostMessaging.setOwnerToken('owner-token-test');
assertTrue('host messaging attaches native ownership tokens',
  openClawHostMessaging.postHostMessage({ kind: 'test' }) === true &&
  postedHostMessage?.nativeOwnerToken === 'owner-token-test' &&
  typeof postedHostMessage?.nativePageToken === 'string' &&
  postedHostMessage.nativePageToken.length > 0);

const excludedTarget = {
  nodeType: 1,
  parentElement: null,
  closest: () => ({})
};
const includedTarget = {
  nodeType: 1,
  parentElement: null,
  closest: () => null
};
assertTrue('mutation filter ignores settings/config/cron/sidebar mutations',
  openClawMutationFilter.isStatusRelevantMutation({ target: excludedTarget, type: 'childList' }) === false &&
  openClawMutationFilter.isStatusRelevantMutation({ target: includedTarget, type: 'attributes', attributeName: 'data-state' }) === true);

// Bug guard: /\b(stop|abort)\b/ never fires on CJK labels because \b is not a
// word boundary between CJK codepoints. A Chinese Control UI's stop button must
// still register, or WORK state stays stuck at idle and stale-stream recovery
// loses its only input.
assertTrue('vocabulary matches CJK stop labels',
  harnessLabelVocabulary.matchesStop('\u505c\u6b62') === true &&
  harnessLabelVocabulary.matchesStop('\u4e2d\u6b62') === true &&
  harnessLabelVocabulary.matchesStop('\u53d6\u6d88') === true &&
  harnessLabelVocabulary.matchesStop('\u751f\u6210\u3092\u505c\u6b62') === true);

assertTrue('vocabulary still matches Latin stop labels on word boundaries',
  harnessLabelVocabulary.matchesStop('Stop') === true &&
  harnessLabelVocabulary.matchesStop('Abort run') === true &&
  harnessLabelVocabulary.matchesStop('nonstop') === false &&
  harnessLabelVocabulary.matchesStop('') === false);

assertTrue('vocabulary matches CJK auth and connecting phrases',
  harnessLabelVocabulary.matchesAuth('\u8bf7\u767b\u5f55\u540e\u7ee7\u7eed') === true &&
  harnessLabelVocabulary.matchesAuth('\u4f1a\u8bdd\u5df2\u8fc7\u671f') === true &&
  harnessLabelVocabulary.matchesConnecting('\u6b63\u5728\u8fde\u63a5...') === true &&
  harnessLabelVocabulary.matchesGatewayError('\u65e0\u6cd5\u8fde\u63a5\u5230\u670d\u52a1\u5668') === true);

assertTrue('vocabulary rejects unrelated text',
  harnessLabelVocabulary.matchesStop('send message') === false &&
  harnessLabelVocabulary.matchesAuth('hello world') === false);

// A backend with no event contract must not report a silent CustomEvent
// dispatch as a handled command.
const genericDispatched = [];
globalThis.window = { dispatchEvent: (event) => { genericDispatched.push(event.type); return true; } };
globalThis.document = { dispatchEvent: (event) => { genericDispatched.push(event.type); return true; } };
const genericHandler = openClawCommandDispatch.createCommandHandler({
  inspectControlUi: () => ({ phase: 'connected', shellDetected: true }),
  postStatus: () => {},
  checkSessionReady: () => {},
  backendProfile: genericBackendProfile
});
const genericHandled = await genericHandler({ command: 'refresh_session', payload: {} });
assertTrue('generic backend dispatches no host-command events and reports unhandled',
  genericHandled === false && genericDispatched.length === 0);

const replies = [];
globalThis.window = { __openClawHostBridge: { ownerToken: 'owner', pageToken: 'page' },
  chrome: { webview: { postMessage: (message) => replies.push(message) } } };
const executeOwned = async (expression, commandId) => {
  const script = asyncExecutionTemplate
    .replace('__HARNESS_COMMAND_MESSAGE_JSON__', JSON.stringify({ kind: 'harness-command-result', commandId, nativeOwnerToken: 'owner', nativePageToken: 'page', deadlineUnixMs: Date.now() + 3000 }))
    .replace('__HARNESS_COMMAND_EXPRESSION__', expression);
  const accepted = eval(script);
  await new Promise((resolve) => setImmediate(resolve));
  return accepted;
};
assertTrue('async command injection returns synchronously', await executeOwned('Promise.resolve(true)', 'success') === true);
await executeOwned('Promise.resolve(false)', 'unhandled');
await executeOwned('Promise.reject(new Error("synthetic failure"))', 'rejected');
assertTrue('async command replies carry settled results and ownership',
  replies.length === 3 && replies[0].handled === true && replies[1].handled === false && replies[2].handled === false &&
  replies.every((reply) => reply.nativeOwnerToken === 'owner' && reply.nativePageToken === 'page'));

process.exit(failed === 0 ? 0 : 1);
'@

$composedRunner = @'
let failed = 0;

const assertTrue = (name, condition, detail = '') => {
  if (!condition) {
    console.error(`FAIL: ${name}${detail ? `: ${detail}` : ''}`);
    failed += 1;
    return;
  }

  console.log(`PASS: ${name}`);
};

const appElement = {
  connected: true,
  tab: 'chat',
  chatMessages: [],
  sessionsResult: {
    defaults: { model: 'gpt-5.5', modelProvider: 'openai' },
    sessions: []
  }
};
const bridgePostedMessages = [];
Object.defineProperty(globalThis, 'crypto', {
  value: { randomUUID: () => 'page-token-test' },
  configurable: true
});
globalThis.MutationObserver = class MutationObserver {
  constructor(callback) {
    this.callback = callback;
  }

  observe() {}
  disconnect() {}
};
globalThis.history = {
  pushState() {},
  replaceState() {}
};
globalThis.window = {
  chrome: {
    webview: {
      postMessage: (message) => {
        bridgePostedMessages.push(message);
      }
    }
  },
  location: { href: 'https://example.test/?session=s1' },
  setTimeout: () => 1,
  clearTimeout: () => {},
  addEventListener: () => {},
  dispatchEvent: () => false
};
globalThis.document = {
  readyState: 'complete',
  visibilityState: 'visible',
  documentElement: {},
  body: {},
  activeElement: null,
  addEventListener: () => {},
  querySelector: (selector) => selector === 'openclaw-app' ? appElement : null,
  querySelectorAll: () => []
};

// The full composed bridge script is executed by the PowerShell harness here.
'@

$composedRunner += "`ntry {`n"
$composedRunner += "  new Function($assembledBridgeScriptLiteral)();`n"
$composedRunner += "} catch (error) {`n"
$composedRunner += "  console.error('FAIL: composed bridge script threw during install');`n"
$composedRunner += "  console.error(error && error.stack || error);`n"
$composedRunner += "  failed += 1;`n"
$composedRunner += "}`n"

$composedRunner += @'

assertTrue('composed bridge exposes native host bridge',
  typeof window.__openClawHostBridge?.reportSessionReady === 'function',
  `window keys: ${Object.keys(window).join(',')}`);
const firstReadyReplay = window.__openClawHostBridge?.reportSessionReady();
const secondReadyReplay = window.__openClawHostBridge?.reportSessionReady();
const readyMessages = bridgePostedMessages.filter((message) => message.kind === 'harness-session-ready');
assertTrue('composed bridge allows native session-ready replay after an earlier ready post',
  firstReadyReplay === true &&
  secondReadyReplay === true &&
  readyMessages.length === 2 &&
  readyMessages.every((message) =>
    message.nativeOwnerToken === 'owner-token-test' &&
    message.nativePageToken === 'page-token-test'));

process.exit(failed === 0 ? 0 : 1);
'@

$backendRegression = Read-Utf8Script (Join-Path $PSScriptRoot 'HostedBackendBridgeRegression.js')
$stopSafetyRegression = Read-Utf8Script (Join-Path $PSScriptRoot 'HostedStopSafetyRegression.js')
$runner = $runner.Replace('process.exit(failed === 0 ? 0 : 1);', $backendRegression + "`n" + $stopSafetyRegression + "`nprocess.exit(failed === 0 ? 0 : 1);")
Invoke-NodeRunner -Source $runner -Name 'bridge-scripts'
Invoke-NodeRunner -Source $composedRunner -Name 'composed-bridge-script'

$typographyScriptLiteral = ConvertTo-Json -InputObject (Read-Utf8Script (Join-Path $servicesRoot 'WebViewTypography.js')) -Compress
$typographyRunner = @'
import vm from 'node:vm';
const template = __TYPOGRAPHY_SCRIPT__;
let created = 0;
let style = null;
const root = { appendChild(node) { style = node; } };
const context = vm.createContext({
  window: { location: { protocol: 'https:', origin: 'https://gateway.example.test' } },
  document: {
    documentElement: root, head: root,
    getElementById() { return style; },
    createElement() { created++; return {}; }
  }
});
function apply(options) {
  vm.runInContext(template.replace('__HARNESS_TYPOGRAPHY_OPTIONS__', JSON.stringify(options)), context);
}
function check(value, message) {
  if (!value) throw new Error(message);
  console.log('PASS: ' + message);
}
apply({ family: 'Noto Sans SC', size: 18, revision: 1 });
check(style.textContent.includes('font-size: 18px') &&
  style.textContent.includes('https://gateway.example.test/__harness__/fonts/NotoSansSC.ttf'),
  'Noto uses the same-origin bundled font route and chosen size');
check(style.textContent.includes('pre *') && style.textContent.includes('code *') &&
  style.textContent.includes('[class*="icon"]'), 'typography preserves code and icon exclusions');
apply({ family: 'System UI', size: 14, revision: 3 });
const current = style.textContent;
check(created === 1 && current.includes('font-family: system-ui,'),
  'changing typography updates one style node without accumulating styles');
apply({ family: 'Noto Sans SC', size: 30, revision: 2 });
check(style.textContent === current, 'a stale typography revision cannot overwrite the latest preference');
apply({ family: 'malicious;}', size: 100, revision: 4 });
check(!style.textContent.includes('malicious') && style.textContent.includes('font-size: 14px'),
  'invalid font settings fall back to System UI and 14 px');
apply({ family: 'Noto Sans SC', size: 14.5, revision: 5 });
check(style.textContent.includes('font-size: 14px') && !style.textContent.includes('font-size: 14.5px'),
  'fractional sizes fall back consistently with native settings');
'@
Invoke-NodeRunner -Source $typographyRunner.Replace('__TYPOGRAPHY_SCRIPT__', $typographyScriptLiteral) -Name 'webview-typography'
