const openClawCommandDispatch = (() => {
  const bridgeTargets = (commandGlobals) => {
    const globals = Array.isArray(commandGlobals) ? commandGlobals : [];
    return [
      window.chat,
      ...globals.map((name) => window[name]?.chat),
      ...globals.map((name) => window[name])
    ].filter(Boolean);
  };

  const invokeBridgeMethod = async (methodNames, payload, commandGlobals) => {
    for (const target of bridgeTargets(commandGlobals)) {
      for (const methodName of methodNames) {
        const method = target?.[methodName];
        if (typeof method !== 'function') continue;

        try {
          const result = method.call(target, payload);
          if (result && typeof result.then === 'function') {
            await result;
          }
          return true;
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
  const clickAffordance = (matcher) => {
    const dom = openClawDomUtilities;
    const candidates = Array.from(
      document.querySelectorAll('button, [role="button"], a[href="#"], [aria-label], [title]'));
    const target = candidates.find((el) => dom.isVisible(el) && matcher(dom.labelOf(el)));
    if (!target) return false;

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
    const commandGlobals = backendProfile?.commandGlobals || [];
    const eventPrefix = backendProfile?.hostCommandEventPrefix || '';

    const runCommand = async (command, payload, { replayReady, domFallback }) => {
      const methodNames = COMMAND_METHODS[command] || [];
      let handled = await invokeBridgeMethod(methodNames, payload, commandGlobals);

      // Event dispatch stays best-effort and does NOT count as handled: a page
      // that never registered a listener consumes the event silently, so treating
      // dispatch as success would report a no-op as a completed command.
      if (!handled) {
        dispatchBridgeEvent(command, payload, eventPrefix);
      }

      // Clicking the affordance a user would click IS observable, so it counts.
      if (!handled && domFallback) {
        handled = domFallback();
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

      switch (command) {
        case 'refresh_session':
        case 'lightweight_sync':
          return await runCommand(command, payload, { replayReady: true });
        case 'fetch_recent_messages':
        case 'reconnect_intent':
          return await runCommand(command, payload, { replayReady: false });
        case 'abort_run':
          return await runCommand(command, payload, {
            replayReady: false,
            domFallback: () => clickAffordance(harnessLabelVocabulary.matchesStop)
          });
        default:
          dispatchBridgeEvent(command, payload, eventPrefix);
          return false;
      }
    };
  };

  return { createCommandHandler, dispatchBridgeEvent, invokeBridgeMethod, clickAffordance };
})();
