// Production AbortRun -> CommandDispatch -> StopInjection, with mutable SPA
// state and controlled promises. No browser, network, or real tasks are used.
const makeStopFixture = () => {
  const calls = { aborts: [], clicks: [], submits: [], replies: [] };
  let abortMethod;
  let showStop = true;
  let appPresent = true;
  const app = { tab: 'chat', sessionKey: 'session-a', chatRunId: 'run-a' };
  const stop = {
    ...makeVisibleElement({ ariaLabel: 'Stop' }), disabled: false,
    click: () => calls.clicks.push([app.sessionKey, app.chatRunId])
  };
  const input = {
    ...makeVisibleElement(), value: '', isConnected: true,
    focus() {}, dispatchEvent: () => true,
    closest: () => ({ requestSubmit: () => calls.submits.push([app.sessionKey, app.chatRunId]) })
  };
  app.querySelectorAll = (selector) => selector.startsWith('textarea') ? [input] : showStop ? [stop] : [];
  globalThis.document = {
    querySelector: (selector) => selector === 'openclaw-app' && appPresent ? app : null,
    querySelectorAll: () => [stop], dispatchEvent: () => true
  };
  globalThis.window = {
    chat: {}, location: { href: 'https://openclaw.example.test/?session=session-a' },
    getComputedStyle: () => ({ display: 'block', visibility: 'visible' }),
    setTimeout: () => {}, dispatchEvent: () => true,
    chrome: { webview: { postMessage: (message) => calls.replies.push(message) } }
  };
  Object.defineProperty(window.chat, 'abort', { get: () => abortMethod });
  const handler = openClawCommandDispatch.createCommandHandler({
    inspectControlUi: () => ({}), postStatus: () => {}, backendProfile: { kind: 'OpenClaw' }
  });
  window.__openClawHostBridge = {
    ownerToken: 'stop-owner', pageToken: 'stop-page', getBackendKind: () => 'OpenClaw', onCommand: handler
  };
  return {
    app, calls, input, stop,
    setAbort(method) { abortMethod = (payload) => { calls.aborts.push(payload); return method(payload); }; },
    hideStop() { showStop = false; }, removeApp() { appPresent = false; },
    execute: () => eval(abortRunScript)
  };
};

for (const replacement of ['session', 'run']) {
  const fixture = makeStopFixture();
  let reject;
  fixture.setAbort(() => new Promise((_, rejectPromise) => { reject = rejectPromise; }));
  const pending = fixture.execute();
  if (replacement === 'session') fixture.app.sessionKey = 'session-b';
  else fixture.app.chatRunId = 'run-b';
  reject(new Error('original request failed'));
  assertTrue(`pending stop rejects ${replacement} replacement without DOM or slash fallback`,
    await pending === false && fixture.calls.aborts.length === 1 &&
    fixture.calls.clicks.length === 0 && fixture.calls.submits.length === 0 && fixture.input.value === '');
}

{
  const fixture = makeStopFixture();
  fixture.setAbort(() => Promise.resolve(true));
  assertTrue('successful API stop receives the original session and run and uses no fallback',
    await fixture.execute() === true && fixture.calls.aborts[0].sessionKey === 'session-a' &&
    fixture.calls.aborts[0].runId === 'run-a' && !fixture.calls.clicks.length && !fixture.calls.submits.length);
}

{
  const fixture = makeStopFixture();
  fixture.setAbort(() => { fixture.app.chatRunId = null; return Promise.resolve(true); });
  assertTrue('successful API stop may clear its completed run without being reported as a failed fallback',
    await fixture.execute() === true && !fixture.calls.clicks.length && !fixture.calls.submits.length);
}

for (const failure of ['reject', 'false']) {
  const fixture = makeStopFixture();
  fixture.setAbort(() => failure === 'reject' ? Promise.reject(new Error('failed')) : Promise.resolve(false));
  assertTrue(`same-target API ${failure} falls back to its unique enabled app stop button`,
    await fixture.execute() === true && fixture.calls.clicks.length === 1 && !fixture.calls.submits.length);
}

{
  const fixture = makeStopFixture();
  fixture.hideStop();
  fixture.setAbort(() => Promise.reject(new Error('failed')));
  assertTrue('same-target failure without stop control submits slash stop once within the transaction',
    await fixture.execute() === true && fixture.calls.submits.length === 1 &&
    fixture.calls.submits[0][0] === 'session-a' && fixture.calls.submits[0][1] === 'run-a');
}

{
  const fixture = makeStopFixture();
  fixture.hideStop();
  fixture.input.value = 'keep this draft';
  fixture.setAbort(() => Promise.reject(new Error('failed')));
  assertTrue('all fallbacks fail safely on a same-target nonempty draft',
    await fixture.execute() === false && !fixture.calls.submits.length && fixture.input.value === 'keep this draft');
}

{
  const fixture = makeStopFixture();
  let reject;
  fixture.setAbort(() => new Promise((_, rejectPromise) => { reject = rejectPromise; }));
  const realNow = Date.now;
  let now = realNow();
  Date.now = () => now;
  try {
    const pending = fixture.execute();
    now += 3001;
    reject(new Error('late network failure after native timeout'));
    assertTrue('late failure after native deadline cannot click or inject even on the original run',
      await pending === false && !fixture.calls.clicks.length && !fixture.calls.submits.length);
  } finally { Date.now = realNow; }
}

{
  const fixture = makeStopFixture();
  let reject;
  fixture.setAbort(() => new Promise((_, rejectPromise) => { reject = rejectPromise; }));
  const pending = fixture.execute();
  window.__openClawHostBridge = { ...window.__openClawHostBridge, pageToken: 'replacement-page' };
  reject(new Error('old page failed'));
  assertTrue('bridge replacement retires every pending stop fallback',
    await pending === false && !fixture.calls.clicks.length && !fixture.calls.submits.length);
}

for (const missing of ['app', 'session', 'run']) {
  const fixture = makeStopFixture();
  fixture.setAbort(() => true);
  if (missing === 'app') fixture.removeApp();
  if (missing === 'session') fixture.app.sessionKey = '';
  if (missing === 'run') fixture.app.chatRunId = null;
  assertTrue(`missing ${missing} identity cannot invoke globals or an unrelated page stop`,
    await fixture.execute() === false && !fixture.calls.aborts.length && !fixture.calls.clicks.length && !fixture.calls.submits.length);
}

for (const mismatch of ['owner', 'page', 'deadline']) {
  const fixture = makeStopFixture();
  globalThis.stopSideEffects = 0;
  const message = { kind: 'harness-command-result', commandId: mismatch,
    nativeOwnerToken: 'stop-owner', nativePageToken: 'stop-page', deadlineUnixMs: Date.now() + 3000 };
  if (mismatch === 'owner') message.nativeOwnerToken = 'retired-owner';
  if (mismatch === 'page') message.nativePageToken = 'retired-page';
  if (mismatch === 'deadline') message.deadlineUnixMs = Date.now() - 1;
  eval(asyncExecutionTemplate.replace('__HARNESS_COMMAND_MESSAGE_JSON__', JSON.stringify(message))
    .replace('__HARNESS_COMMAND_EXPRESSION__', '(globalThis.stopSideEffects++, true)'));
  await new Promise((resolve) => setImmediate(resolve));
  assertTrue(`executor blocks ${mismatch} mismatch before evaluating a side effect`,
    globalThis.stopSideEffects === 0 && fixture.calls.replies.length === 1 && !fixture.calls.replies[0].handled);
}

{
  const fixture = makeStopFixture();
  fixture.hideStop();
  fixture.input.focus = () => { fixture.app.sessionKey = 'session-b'; };
  assertTrue('a focus handler that changes sessions cannot cause slash injection into the replacement composer',
    await fixture.execute() === false && fixture.input.value === '' && !fixture.calls.submits.length);
}
delete globalThis.stopSideEffects;
