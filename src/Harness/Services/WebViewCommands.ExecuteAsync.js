(() => {
  const message = __HARNESS_COMMAND_MESSAGE_JSON__;
  const webview = window.chrome.webview;
  const complete = (handled) => webview.postMessage({ ...message, handled: handled === true });
  const harnessCommandContext = {
    isCurrent: () => window.__openClawHostBridge?.ownerToken === message.nativeOwnerToken &&
      window.__openClawHostBridge?.pageToken === message.nativePageToken &&
      Date.now() < message.deadlineUnixMs
  };
  Promise.resolve()
    .then(() => harnessCommandContext.isCurrent() ? (__HARNESS_COMMAND_EXPRESSION__) : false)
    .then(complete, () => complete(false));
  return true;
})()
