const harnessBackendAdapter = (() => {
  // Hermes Webapp shares the official Desktop renderer. These markers come
  // from its browser host bridge and primary composer, not from page prose.
  const hermesHostSelector = 'html[data-hermes-desktop-host="browser"]';
  const composerSelector = '[data-slot="composer-root"][data-tour="composer"]';
  const stopLabel = /^(?:stop|stop generating|停止|停止生成|中止|生成を停止|중지)$/i;
  const sendLabel = /^(?:send|send message|发送|发送消息|傳送|送信|보내기)$/i;

  const resolveKind = (profile) => {
    const kind = profile?.kind;
    if (kind === 'OpenClaw' || kind === 'Hermes') return kind;
    if (kind !== 'Auto') return 'Generic';
    if (document.querySelector('openclaw-app')) return 'OpenClaw';
    if (document.querySelector(hermesHostSelector)) return 'Hermes';
    return 'Generic';
  };

  const openClawProfile = (profile) => ({
    ...profile,
    kind: 'OpenClaw',
    appStateElement: 'openclaw-app',
    hostCommandEventPrefix: 'openclaw',
    commandGlobals: ['__openclaw', '__OPENCLAW__', '__APP__', 'app']
  });

  const primaryHermesComposer = () => {
    if (!document.querySelector(hermesHostSelector)) return null;
    const composers = Array.from(document.querySelectorAll(composerSelector))
      .filter((element) => openClawDomUtilities.isVisible(element));
    // Never choose an arbitrary split-pane conversation.
    return composers.length === 1 ? composers[0] : null;
  };

  const hermesSubmitButton = () => {
    const composer = primaryHermesComposer();
    const buttons = Array.from(composer?.querySelectorAll('button[type="submit"]') || [])
      .filter((element) => openClawDomUtilities.isVisible(element) &&
        !element.disabled && element.getAttribute('aria-disabled') !== 'true');
    return buttons.length === 1 ? buttons[0] : null;
  };

  const hermesStopButton = () => {
    const button = hermesSubmitButton();
    return button && stopLabel.test(openClawDomUtilities.compactText(button.getAttribute('aria-label')))
      ? button : null;
  };

  const abortHermesRun = () => {
    const button = hermesStopButton();
    if (!button) return false;
    button.click();
    return true;
  };

  // A document can host many conversations without navigating. Its page token
  // is not a run identity; require the concrete OpenClaw app/session/run tuple.
  const captureOpenClawStopTarget = () => {
    const app = document.querySelector('openclaw-app');
    const sessionKey = typeof app?.sessionKey === 'string' ? app.sessionKey.trim() : '';
    const runId = typeof app?.chatRunId === 'string' ? app.chatRunId.trim() : '';
    return app?.tab === 'chat' && sessionKey && runId ? { app, sessionKey, runId } : null;
  };

  const isCurrentOpenClawStopTarget = (target) => {
    const current = captureOpenClawStopTarget();
    return Boolean(target && current && current.app === target.app &&
      current.sessionKey === target.sessionKey && current.runId === target.runId);
  };

  const inspectPage = ({ kind, statusKind, strings, inputFocused, focusedInputHasText }) => {
    const dom = openClawDomUtilities;
    const loaded = Boolean(document.body) && document.readyState !== 'loading';
    const composer = kind === 'Hermes' ? primaryHermesComposer() : null;
    const button = composer ? hermesSubmitButton() : null;
    const canAbortRun = Boolean(button && stopLabel.test(dom.compactText(button.getAttribute('aria-label'))));
    const idle = Boolean(button && sendLabel.test(dom.compactText(button.getAttribute('aria-label'))));
    const modelButton = composer?.querySelector('button[data-tour="model-pill"]');
    const modelLabel = modelButton && dom.isVisible(modelButton)
      ? dom.compactText(dom.textOf(modelButton.querySelector('span.truncate'))).slice(0, 96) : '';
    const url = window.location?.href || '';
    // Login remains owned by the hosted page. OpenClaw token, pairing, proxy,
    // and reconnect heuristics must not classify other products' page content.
    const authRequired = kind === 'Hermes' && loaded && !composer &&
      (/\/(?:login|signin|auth)(?:\/|$|\?)/i.test(url) ||
        Array.from(document.querySelectorAll('input[type="password"]')).some(dom.isVisible));
    return {
      kind: statusKind,
      backendKind: kind,
      phase: !loaded ? 'loading' : authRequired ? 'auth_required' : 'connected',
      summary: !loaded ? strings.bridgePageLoading : authRequired
        ? strings.bridgeWebAuthRequiredSummary : strings.bridgeGatewayUiLoaded,
      detail: authRequired ? strings.bridgeWebAuthRequiredDetail : '',
      url,
      shellDetected: loaded && !authRequired,
      isBusy: canAbortRun,
      canAbortRun,
      // A quiet tool can run for minutes. DOM silence is not a stalled Hermes
      // connection and must never trigger automatic reload of its active turn.
      isBusyStaleCandidate: false,
      isBusyStale: false,
      busyStaleSeconds: 0,
      inputFocused,
      focusedInputHasText,
      workState: canAbortRun ? 'busy' : idle ? 'idle' : 'unknown',
      activitySignature: '',
      currentModel: modelLabel,
      currentModelSource: modelLabel ? 'dom:hermes-model-pill' : '',
      currentEffort: ''
    };
  };

  return { resolveKind, openClawProfile, inspectPage, abortHermesRun,
    captureOpenClawStopTarget, isCurrentOpenClawStopTarget };
})();
