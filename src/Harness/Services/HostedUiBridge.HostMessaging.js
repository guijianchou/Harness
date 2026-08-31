const openClawHostMessaging = (() => {
  const KIND = 'harness-control-ui-status';
  const SESSION_READY_KIND = 'harness-session-ready';
  const GAP_KIND = 'harness-event-gap';
  let ownerToken = '';
  const pageToken = (globalThis.crypto && typeof globalThis.crypto.randomUUID === 'function')
    ? globalThis.crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`;

  const setOwnerToken = (value) => {
    ownerToken = typeof value === 'string' ? value : '';
  };

  const attachOwnership = (message) => ({
    ...message,
    nativeOwnerToken: ownerToken,
    nativePageToken: pageToken
  });

  const postHostMessage = (message) => {
    try {
      if (!window.chrome?.webview?.postMessage) return false;
      window.chrome.webview.postMessage(attachOwnership(message));
      return true;
    } catch {
      return false;
    }
  };

  return { KIND, SESSION_READY_KIND, GAP_KIND, pageToken, setOwnerToken, postHostMessage };
})();
