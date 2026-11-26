(() => {
  // This script is only a fallback inside the original stop transaction.
  // Never capture a replacement session/run after an asynchronous abort failed.
  if (typeof isCurrentStopTarget !== 'function' || !isCurrentStopTarget()) return false;
  // Slash commands are an OpenClaw fallback only. Never submit text into an
  // ordinary web form or Hermes' composer/terminal.
  if (window.__openClawHostBridge?.getBackendKind?.() !== 'OpenClaw') return false;
  const app = document.querySelector('openclaw-app');
  if (!app || app.tab !== 'chat' || !(app.chatRunId || app.chatSending || app.chatStream != null)) return false;
  const stopCommand = '/stop';

  const isVisible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  };

  const clearElement = (el) => {
    if (!isCurrentStopTarget() || !el?.isConnected || ('value' in el ? el.value : el.textContent) !== stopCommand) return;

    if ('value' in el) {
      const prototype = Object.getPrototypeOf(el);
      const descriptor = Object.getOwnPropertyDescriptor(prototype, 'value');
      if (descriptor && typeof descriptor.set === 'function') {
        descriptor.set.call(el, '');
      } else {
        el.value = '';
      }
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return;
    }

    el.textContent = '';
    el.dispatchEvent(new InputEvent('input', { bubbles: true, data: '', inputType: 'deleteContentBackward' }));
  };

  const submitElement = (el) => {
    if (!el || !isCurrentStopTarget()) return false;
    const form = el.closest('form');
    if (form) {
      if (typeof form.requestSubmit === 'function') {
        form.requestSubmit();
      } else if (form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) && typeof form.submit === 'function') {
        form.submit();
      }
      window.setTimeout(() => clearElement(el), 0);
      return true;
    }

    const keyboardEventInit = {
      key: 'Enter',
      code: 'Enter',
      keyCode: 13,
      which: 13,
      bubbles: true,
      cancelable: true
    };

    el.dispatchEvent(new KeyboardEvent('keydown', keyboardEventInit));
    if (!isCurrentStopTarget()) return true;
    el.dispatchEvent(new KeyboardEvent('keypress', keyboardEventInit));
    if (!isCurrentStopTarget()) return true;
    el.dispatchEvent(new KeyboardEvent('keyup', keyboardEventInit));
    window.setTimeout(() => clearElement(el), 0);
    return true;
  };

  const setNativeValue = (el, value) => {
    const prototype = Object.getPrototypeOf(el);
    const descriptor = Object.getOwnPropertyDescriptor(prototype, 'value');
    if (descriptor && typeof descriptor.set === 'function') {
      descriptor.set.call(el, value);
    } else {
      el.value = value;
    }
  };

  const inputs = Array.from(app.querySelectorAll('textarea, [contenteditable="true"], [role="textbox"]'))
    .filter((el) => !el.disabled && !el.readOnly && el.getAttribute('aria-disabled') !== 'true' && isVisible(el));
  if (inputs.length !== 1) return false;
  const target = inputs[0];
  if (String('value' in target ? target.value : target.textContent || '').trim()) return false;
  const textInput = 'value' in target ? target : null;

  if (!isCurrentStopTarget()) return false;
  if (textInput) {
    textInput.focus();
    if (!isCurrentStopTarget()) return false;
    setNativeValue(textInput, stopCommand);
    textInput.dispatchEvent(new Event('input', { bubbles: true }));
    if (!isCurrentStopTarget()) return false;
    textInput.dispatchEvent(new Event('change', { bubbles: true }));
    return submitElement(textInput);
  }

  const editor = textInput ? null : target;

  if (editor) {
    editor.focus();
    if (!isCurrentStopTarget()) return false;
    editor.textContent = stopCommand;
    editor.dispatchEvent(new InputEvent('input', { bubbles: true, data: stopCommand, inputType: 'insertText' }));
    return submitElement(editor);
  }

  return false;
})()
