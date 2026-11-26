(() => {
  const bridge = window.__openClawHostBridge;
  const backendKind = bridge?.getBackendKind?.();
  if (backendKind !== 'OpenClaw' && backendKind !== 'Hermes') return false;
  if (typeof bridge.onCommand !== 'function') return false;

  // All fallbacks share the handler's original session/run identity. Native
  // timeout must never start another command that captures a different run.
  const deadline = Date.now() + 3000;
  const isCurrentCommand = () => window.__openClawHostBridge === bridge && Date.now() < deadline &&
    (typeof harnessCommandContext === 'undefined' || harnessCommandContext.isCurrent());
  if (!isCurrentCommand()) return false;
  const result = bridge.onCommand({ command: 'abort_run', payload: {
    isCurrentCommand,
    trySlashStop: (isCurrentStopTarget) => __HARNESS_STOP_INJECTION_SCRIPT__
  } });
  return result && typeof result.then === 'function'
    ? result.then((handled) => handled === true)
    : result === true;
})()
