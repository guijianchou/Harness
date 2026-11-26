const openClawStatusInspection = (() => {
  const createInspector = ({ strings, mutationFilter, modelResolver, statusKind, backendProfile }) => {
    const dom = openClawDomUtilities;
    const vocabulary = harnessLabelVocabulary;
    const profile = backendProfile || {};
    const agentProfile = harnessBackendAdapter.openClawProfile(profile);
    const modelReader = openClawModelDomFallback.createReader({ dom, mutationFilter, modelResolver, profile: agentProfile });
    const activity = openClawActivityState.createActivityTracker({ dom, mutationFilter, profile: agentProfile });
    const phaseClassifier = openClawPhaseClassifier.createClassifier({ dom, mutationFilter, strings });
    const isStatusProbeExcludedElement = mutationFilter.isStatusProbeExcludedElement;

    const hasVisibleElement = (selector, predicate) => {
      return Array.from(document.querySelectorAll(selector))
        .some((el) => !isStatusProbeExcludedElement(el) && (!predicate || predicate(el)) && dom.isVisible(el));
    };

    const readFocusedInputState = () => {
      const activeElement = document.activeElement;
      const inputFocused = dom.isEditableElement(activeElement) && dom.isVisible(activeElement);
      const focusedInputHasText = inputFocused && dom.compactText(
        activeElement instanceof HTMLInputElement || activeElement instanceof HTMLTextAreaElement
          ? activeElement.value
          : activeElement?.textContent || '').length > 0;

      return { inputFocused, focusedInputHasText };
    };

    const detectShellFromDom = () => {
      return (
        hasVisibleElement('textarea, input:not([type]), input[type="text"], [contenteditable="true"], [role="textbox"]') ||
        hasVisibleElement('button, [role="button"], nav, aside, [role="navigation"]', (el) =>
          vocabulary.matchesShellAffordance(dom.labelOf(el))));
    };

    const detectBusyFromDom = (needsDomSignals) => {
      const busyByButton = needsDomSignals && hasVisibleElement('button, [role="button"], [aria-label], [title]', (el) =>
        vocabulary.matchesStop(dom.labelOf(el)));
      const busyBySignals = needsDomSignals && hasVisibleElement(
        '[aria-busy="true"], [role="progressbar"], [data-busy="true"], [data-running="true"], [data-state="running"], [data-state="streaming"], [data-status="running"], [data-status="streaming"]');
      const busyByStatusText = needsDomSignals && hasVisibleElement(
        '[role="status"], [aria-live="polite"], [aria-live="assertive"]', (el) =>
          vocabulary.matchesBusy(dom.textOf(el)));
      return busyByButton || busyBySignals || busyByStatusText;
    };

    const inspectControlUi = () => {
      const backendKind = harnessBackendAdapter.resolveKind(profile);
      const { inputFocused, focusedInputHasText } = readFocusedInputState();
      if (backendKind !== 'OpenClaw') {
        return harnessBackendAdapter.inspectPage({ backendKind, kind: backendKind, statusKind, strings, inputFocused, focusedInputHasText });
      }
      const url = window.location ? window.location.href : '';
      const lowerUrl = url.toLowerCase();
      const appState = activity.readOpenClawAppStateStatus();
      const needsDomSignals = !appState || !appState.connected || Boolean(appState.lastError);
      const text = needsDomSignals ? phaseClassifier.collectSignalText() : '';
      const shellDetected = appState?.shellDetected || (needsDomSignals && detectShellFromDom());
      const apiBusy = activity.detectBusyFromApi();
      const domBusy = detectBusyFromDom(needsDomSignals);
      const isBusy = Boolean(appState?.isBusy) || apiBusy || domBusy;
      const isBusyStaleCandidate = Boolean(appState?.isChatBusy) || apiBusy || domBusy;
      const workState = isBusy ? 'busy' : shellDetected ? 'idle' : 'unknown';
      const activitySignature = [
        appState?.activitySignature,
        isBusy ? activity.collectDomActivitySignature() : ''
      ].filter(Boolean).join('|').slice(0, 512);
      const modelSnapshot = modelReader.readCurrentModel();
      const effortSnapshot = modelReader.readCurrentEffort();
      const phaseSnapshot = phaseClassifier.classify({
        documentReadyState: document.readyState,
        hasBody: Boolean(document.body),
        text,
        lowerUrl,
        shellDetected
      });

      return activity.applyBusyStaleness({
        kind: statusKind,
        backendKind,
        ...phaseSnapshot,
        url,
        shellDetected,
        isBusy,
        canAbortRun: appState?.canAbortRun === true,
        isBusyStaleCandidate,
        inputFocused,
        focusedInputHasText,
        workState,
        activitySignature,
        currentModel: modelSnapshot.value,
        currentModelSource: modelSnapshot.source,
        currentEffort: effortSnapshot.value
      });
    };

    return {
      inspectControlUi,
      isEditableElement: dom.isEditableElement,
      compactText: dom.compactText
    };
  };

  return { createInspector };
})();
