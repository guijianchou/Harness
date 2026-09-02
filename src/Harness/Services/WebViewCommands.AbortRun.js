(() => {
  const isVisible = (el) => {
    if (!el) return false;
    const style = window.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  };

  const labelOf = (el) => [
    el?.getAttribute?.('aria-label'),
    el?.getAttribute?.('title'),
    el?.innerText,
    el?.textContent
  ].filter(Boolean).join(' ').trim();

  // Prefer the bridge's own dispatcher when it is present: it knows the backend's
  // command API and event contract, and its DOM fallback is multilingual.
  const bridge = window.__openClawHostBridge;
  if (bridge && typeof bridge.onCommand === 'function') {
    const result = bridge.onCommand({ command: 'abort_run' });
    if (result && typeof result.then === 'function') {
      return result.then((handled) => handled === true);
    }

    if (result === true) return true;
  }

  const abortTargets = [
    window.chat,
    window.__openclaw?.chat,
    window.__OPENCLAW__?.chat,
    window.__APP__?.chat,
    window.app?.chat
  ];

  for (const target of abortTargets) {
    if (target && typeof target.abort === 'function') {
      target.abort();
      return true;
    }
  }

  // \b is not a word boundary between CJK codepoints, so Latin terms match on a
  // boundary and CJK terms match as substrings. A single /\b(stop|abort)\b/ never
  // fires on a Chinese or Japanese Control UI.
  const STOP_LATIN = /\b(?:stop|abort|cancel|halt|interrupt|terminate)\b/i;
  const STOP_CJK = ['停止', '中止', '取消', '中断', '終止', '终止', '중지', '취소'];
  const matchesStop = (value) => {
    const text = (value == null ? '' : String(value)).toLowerCase();
    if (!text) return false;
    return STOP_LATIN.test(text) || STOP_CJK.some((term) => text.includes(term));
  };

  const abortButton = Array.from(document.querySelectorAll('button, [role="button"], [aria-label], [title]'))
    .find((el) => isVisible(el) && matchesStop(labelOf(el)));

  if (abortButton) {
    abortButton.click();
    return true;
  }

  return false;
})()
