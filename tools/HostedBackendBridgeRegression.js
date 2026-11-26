// Executed by verify-bridge-scripts.ps1 with the production bridge modules.
const inspectBackend = (kind) => openClawStatusInspection.createInspector({
  strings: { ...phaseStrings, bridgeWebAuthRequiredSummary: 'Sign in to this web app.',
    bridgeWebAuthRequiredDetail: 'Complete sign-in in the hosted page.' },
  mutationFilter: fakeMutationFilter,
  modelResolver: openClawModelResolver,
  statusKind: 'harness-control-ui-status',
  backendProfile: { kind }
}).inspectControlUi();

let unintendedCalls = 0;
const unrelatedControl = makeVisibleElement({ text: 'Stop', ariaLabel: 'Stop' });
unrelatedControl.click = () => unintendedCalls++;
globalThis.window = {
  location: { href: 'https://example.test/article/token-missing' },
  getComputedStyle: () => ({ display: 'block', visibility: 'visible' }),
  chat: { busy: true, abort: () => unintendedCalls++, refreshSession: () => unintendedCalls++ },
  app: { chat: { busy: true, stop: () => unintendedCalls++ } }
};
globalThis.document = {
  readyState: 'complete', body: {}, activeElement: null,
  querySelector: () => null,
  querySelectorAll: () => [unrelatedControl]
};
for (const kind of ['Generic', 'DeepSeek', 'Auto', 'future-backend']) {
  const snapshot = inspectBackend(kind);
  assertTrue(`${kind} ordinary page becomes ready without agent telemetry`,
    snapshot.phase === 'connected' && snapshot.shellDetected &&
    snapshot.currentModel === '' && snapshot.workState === 'unknown' &&
    snapshot.isBusy === false && snapshot.canAbortRun === false && snapshot.isBusyStale === false);
  const handler = openClawCommandDispatch.createCommandHandler({
    inspectControlUi: () => snapshot, postStatus: () => {}, backendProfile: { kind }
  });
  assertTrue(`${kind} rejects page commands`,
    await handler({ command: 'abort_run' }) === false &&
    await handler({ command: 'refresh_session' }) === false);
  window.__openClawHostBridge = { getBackendKind: () => kind };
  assertTrue(`${kind} cannot use standalone abort or slash-command scripts`,
    await eval(abortRunScript) === false && eval(stopInjectionScript) === false);
}
assertEqual('ordinary pages never invoke unrelated globals or controls', unintendedCalls, 0);

let hermesStopCount = 0;
let hermesLabel = 'Stop';
let hermesDisabled = false;
let hermesComposerCount = 1;
let hermesHost = true;
const hermesSubmit = {
  ...makeVisibleElement(),
  get disabled() { return hermesDisabled; },
  getAttribute: (name) => name === 'aria-label' ? hermesLabel : null,
  click: () => hermesStopCount++
};
const hermesModel = makeVisibleElement({ child: makeVisibleElement({ text: 'Claude Opus 4.7' }) });
const hermesComposer = {
  ...makeVisibleElement(),
  querySelectorAll: (selector) => selector === 'button[type="submit"]' ? [hermesSubmit] : [],
  querySelector: (selector) => selector === 'button[data-tour="model-pill"]' ? hermesModel : null
};
document.querySelector = (selector) => selector === 'html[data-hermes-desktop-host="browser"]' && hermesHost ? {} : null;
document.querySelectorAll = (selector) => selector === '[data-slot="composer-root"][data-tour="composer"]'
  ? Array(hermesComposerCount).fill(hermesComposer) : [];
const hermesSnapshot = inspectBackend('Hermes');
assertTrue('Hermes official primary composer reports model and active stop capability',
  hermesSnapshot.currentModel === 'Claude Opus 4.7' &&
  hermesSnapshot.currentModelSource === 'dom:hermes-model-pill' &&
  hermesSnapshot.workState === 'busy' && hermesSnapshot.canAbortRun &&
  hermesSnapshot.isBusyStaleCandidate === false);
assertEqual('Auto recognizes the official Hermes browser host marker', inspectBackend('Auto').backendKind, 'Hermes');
assertEqual('explicit Generic never upgrades itself to an agent', inspectBackend('Generic').currentModel, '');
const hermesHandler = openClawCommandDispatch.createCommandHandler({
  inspectControlUi: () => inspectBackend('Hermes'), postStatus: () => {}, backendProfile: { kind: 'Hermes' }
});
assertTrue('Hermes stops exactly the observed primary composer', await hermesHandler({ command: 'abort_run' }));
assertEqual('Hermes stop click is performed once', hermesStopCount, 1);
assertTrue('Hermes does not use OpenClaw reconnect methods', await hermesHandler({ command: 'refresh_session' }) === false);
assertEqual('Hermes does not call unrelated page globals', unintendedCalls, 0);
hermesLabel = 'Stop listening';
assertTrue('Hermes voice stop is not an agent run stop', !inspectBackend('Hermes').canAbortRun &&
  await hermesHandler({ command: 'abort_run' }) === false);
hermesLabel = '停止';
assertTrue('Hermes Chinese stop label is supported', inspectBackend('Hermes').canAbortRun);
hermesDisabled = true;
assertTrue('Hermes disabled stop is not actionable', !inspectBackend('Hermes').canAbortRun);
hermesDisabled = false;
hermesLabel = 'Send';
assertEqual('Hermes observed send control reports idle', inspectBackend('Hermes').workState, 'idle');
hermesLabel = 'Stop';
hermesComposerCount = 2;
assertTrue('Hermes ambiguous primary composers cannot stop either session', !inspectBackend('Hermes').canAbortRun &&
  await hermesHandler({ command: 'abort_run' }) === false);
hermesComposerCount = 1;
hermesHost = false;
assertTrue('Hermes marketing pages and unknown frontends have no agent controls',
  !inspectBackend('Hermes').canAbortRun && inspectBackend('Hermes').currentModel === '');
assertEqual('Auto returns to ordinary web behavior without product markers', inspectBackend('Auto').backendKind, 'Generic');
window.location.href = 'https://hermes.example.test/login';
assertTrue('Hermes sign-in never requests an OpenClaw gateway token',
  inspectBackend('Hermes').phase === 'auth_required' &&
  inspectBackend('Hermes').summary === 'Sign in to this web app.' &&
  inspectBackend('Hermes').detail === 'Complete sign-in in the hosted page.');
window.location.href = 'https://example.test/';

document.querySelector = (selector) => selector === 'openclaw-app' ? appElement : null;
document.querySelectorAll = () => [];
appElement.chatLoading = true;
appElement.chatRunId = 'active-run';
appElement.sessionKey = 'active-session';
assertTrue('Auto retains OpenClaw model and chat stop capability on a confirmed app',
  inspectBackend('Auto').backendKind === 'OpenClaw' && inspectBackend('Auto').canAbortRun &&
  inspectBackend('Auto').currentModel === 'openai/gpt-5.5');
appElement.chatLoading = false;
appElement.chatRunId = null;
window.chat.busy = false;
appElement.chatLoading = true;
assertTrue('OpenClaw history loading is busy but cannot abort a run',
  inspectBackend('Auto').isBusy && !inspectBackend('Auto').canAbortRun);
appElement.chatLoading = false;
appElement.chatSending = true;
appElement.chatStream = 'pending';
assertTrue('OpenClaw sending or streaming without a run identity stays busy with Stop disabled',
  inspectBackend('Auto').isBusy && !inspectBackend('Auto').canAbortRun);
appElement.chatRunId = 'active-run';
appElement.tab = 'settings';
assertTrue('OpenClaw Stop is unavailable outside the current chat tab', !inspectBackend('Auto').canAbortRun);
appElement.tab = 'chat';
appElement.sessionKey = '';
assertTrue('OpenClaw Stop requires a stable session identity', !inspectBackend('Auto').canAbortRun);
appElement.sessionKey = 'active-session';
appElement.chatRunId = null;
appElement.chatSending = false;
appElement.chatStream = null;
document.querySelector = () => null;
window.chat.busy = true;
assertTrue('an explicitly selected OpenClaw page cannot advertise Stop from unrelated globals',
  !inspectBackend('OpenClaw').canAbortRun);
window.chat.busy = false;
document.querySelector = (selector) => selector === 'openclaw-app' ? appElement : null;

let submittedStops = 0;
let pendingClear = null;
const openClawInput = {
  ...makeVisibleElement(),
  value: 'draft to preserve', isConnected: true,
  focus: () => {}, dispatchEvent: () => true,
  closest: () => ({ requestSubmit: () => submittedStops++, dispatchEvent: () => { submittedStops++; return true; } })
};
appElement.chatRunId = 'active-run';
appElement.tab = 'chat';
appElement.sessionKey = 'active-session';
appElement.querySelectorAll = () => [openClawInput];
window.__openClawHostBridge = { getBackendKind: () => 'OpenClaw' };
window.setTimeout = (callback) => { pendingClear = callback; };
const stopTarget = harnessBackendAdapter.captureOpenClawStopTarget();
const injectOwnedStop = () => {
  const isCurrentStopTarget = () => harnessBackendAdapter.isCurrentOpenClawStopTarget(stopTarget);
  return eval(stopInjectionScript);
};
assertTrue('OpenClaw slash-stop preserves a typed draft',
  injectOwnedStop() === false && openClawInput.value === 'draft to preserve' && submittedStops === 0);
openClawInput.value = '';
assertTrue('OpenClaw slash-stop submits one command in an empty active chat',
  injectOwnedStop() === true && openClawInput.value === '/stop' && submittedStops === 1);
openClawInput.value = 'new draft';
pendingClear();
assertEqual('deferred stop cleanup preserves a new draft', openClawInput.value, 'new draft');
appElement.tab = 'settings';
openClawInput.value = '';
assertTrue('OpenClaw slash-stop never submits a settings field', injectOwnedStop() === false);
assertTrue('standalone slash-stop cannot acquire a fresh run identity', eval(stopInjectionScript) === false);
assertTrue('standalone abort cannot bypass a missing backend command handler', await eval(abortRunScript) === false);
