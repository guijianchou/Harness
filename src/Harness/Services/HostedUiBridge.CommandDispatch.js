const openClawCommandDispatch = (() => {
  const bridgeTargets = (commandGlobals) => {
    const globals = Array.isArray(commandGlobals) ? commandGlobals : [];
    return [
      window.chat,
      ...globals.map((name) => window[name]?.chat),
      ...globals.map((name) => window[name])
    ].filter(Boolean);
  };

  const invokeBridgeMethod = async (methodNames, payload, commandGlobals, isCurrent = () => true) => {
    for (const target of bridgeTargets(commandGlobals)) {
      for (const methodName of methodNames) {
        if (!isCurrent()) return false;
        const method = target?.[methodName];
        if (typeof method !== 'function') continue;

        try {
          let result = method.call(target, payload);
          if (result && typeof result.then === 'function') {
            result = await result;
          }
          // A successful abort commonly clears chatRunId before it resolves.
          // That is a completed operation; only failures may attempt a fallback.
          if (result !== false) return true;
        } catch {
        }
      }
    }

    return false;
  };

  // The CustomEvent contract is backend-specific. Dispatching a prefix the page
  // does not listen for is a silent no-op, so a backend without a prefix reports
  // "not dispatched" and lets the caller fall back to a DOM affordance instead.
  const dispatchBridgeEvent = (command, payload, eventPrefix) => {
    if (!eventPrefix) return false;

    const detail = { command, payload };
    let dispatched = false;

    for (const target of [window, document]) {
      if (!target?.dispatchEvent) continue;
      target.dispatchEvent(new CustomEvent(`${eventPrefix}:host-command`, { detail }));
      target.dispatchEvent(new CustomEvent(`${eventPrefix}:${command}`, { detail }));
      dispatched = true;
    }

    return dispatched;
  };

  // Last resort for backends with neither a scriptable API nor an event contract:
  // click the visible affordance a user would click. Multilingual by vocabulary,
  // so a Chinese or Japanese Control UI is reachable too.
  const clickAffordance = (matcher, root = document, isCurrent = () => true) => {
    const dom = openClawDomUtilities;
    const candidates = Array.from(
      root.querySelectorAll('button, [role="button"], a[href="#"], [aria-label], [title]'))
      .filter((el) => dom.isVisible(el) && !el.disabled &&
        el.getAttribute('aria-disabled') !== 'true' && matcher(dom.labelOf(el)));
    if (candidates.length !== 1 || !isCurrent()) return false;
    const target = candidates[0];

    try {
      target.click();
      return true;
    } catch {
      return false;
    }
  };

  const COMMAND_METHODS = {
    refresh_session: ['refreshSession', 'reloadSession', 'reconnect', 'connect', 'resume'],
    fetch_recent_messages: ['fetchRecentMessages', 'loadRecentMessages', 'syncMessages', 'sync'],
    lightweight_sync: ['sync', 'refresh', 'refreshSession', 'fetchRecentMessages', 'loadRecentMessages'],
    reconnect_intent: ['reconnect', 'connect', 'resume', 'refreshSession'],
    abort_run: ['abort', 'stop', 'cancel', 'abortRun', 'stopRun']
  };

  const createCommandHandler = ({ inspectControlUi, postStatus, checkSessionReady, backendProfile }) => {
    const profile = harnessBackendAdapter.openClawProfile(backendProfile);
    const commandGlobals = profile.commandGlobals;
    const eventPrefix = profile.hostCommandEventPrefix;

    const stopRun = async (options) => {
      const target = harnessBackendAdapter.captureOpenClawStopTarget();
      const deadline = Date.now() + 3000;
      const isCurrent = () => Date.now() < deadline &&
        (typeof options?.isCurrentCommand !== 'function' || options.isCurrentCommand()) &&
        harnessBackendAdapter.isCurrentOpenClawStopTarget(target);
      if (!isCurrent()) return false;

      const payload = { sessionKey: target.sessionKey, runId: target.runId };
      let handled = await invokeBridgeMethod(COMMAND_METHODS.abort_run, payload, commandGlobals, isCurrent);
      if (!handled && !isCurrent()) return false;
      // Unacknowledged CustomEvents cannot establish which run was stopped.
      if (!handled) handled = clickAffordance(harnessLabelVocabulary.matchesStop, target.app, isCurrent);
      if (!handled && isCurrent() && typeof options?.trySlashStop === 'function') {
        handled = options.trySlashStop(isCurrent) === true;
      }
      postStatus(inspectControlUi());
      return handled;
    };

    const runCommand = async (command, payload, { replayReady }) => {
      const methodNames = COMMAND_METHODS[command] || [];
      let handled = await invokeBridgeMethod(methodNames, payload, commandGlobals);

      // Event dispatch stays best-effort and does NOT count as handled: a page
      // that never registered a listener consumes the event silently, so treating
      // dispatch as success would report a no-op as a completed command.
      if (!handled) {
        dispatchBridgeEvent(command, payload, eventPrefix);
      }

      const snapshot = inspectControlUi();
      postStatus(snapshot);
      if (replayReady && checkSessionReady) {
        checkSessionReady(snapshot);
      }

      return handled;
    };

    return async (message) => {
      const command = message?.command || '';
      const payload = message?.payload;
      const backendKind = harnessBackendAdapter.resolveKind(backendProfile);
      if (backendKind !== 'OpenClaw') {
        const handled = backendKind === 'Hermes' && command === 'abort_run' &&
          (typeof payload?.isCurrentCommand !== 'function' || payload.isCurrentCommand())
          ? harnessBackendAdapter.abortHermesRun() : false;
        postStatus(inspectControlUi());
        return handled;
      }

      switch (command) {
        case 'refresh_session':
        case 'lightweight_sync':
          return await runCommand(command, payload, { replayReady: true });
        case 'fetch_recent_messages':
        case 'reconnect_intent':
          return await runCommand(command, payload, { replayReady: false });
        case 'abort_run':
          return await stopRun(payload);
        default:
          dispatchBridgeEvent(command, payload, eventPrefix);
          return false;
      }
    };
  };

  return { createCommandHandler, dispatchBridgeEvent, invokeBridgeMethod, clickAffordance };
})();
