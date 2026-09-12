(() => {
  const message = __HARNESS_COMMAND_MESSAGE_JSON__;
  const webview = window.chrome.webview;
  const complete = (handled) => webview.postMessage({ ...message, handled: handled === true });
  Promise.resolve()
    .then(() => (__HARNESS_COMMAND_EXPRESSION__))
    .then(complete, () => complete(false));
  return true;
})()
