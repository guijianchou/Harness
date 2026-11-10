const openClawDomUtilities = (() => {
  const isVisible = (el) => {
    if (!el) return false;
    if (typeof el.checkVisibility === 'function') {
      return el.checkVisibility({ visibilityProperty: true });
    }
    const style = window.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  };

  // innerText forces layout and can realize every content-visibility:auto chat
  // subtree. Read a bounded text sample without measuring the conversation.
  const textSampleOf = (el, tail = false, limit = 2048) => {
    if (!el) return { text: '', length: 0 };
    if (!el.nodeType || typeof document.createTreeWalker !== 'function') {
      const text = String(el.textContent || '');
      return { text: tail ? text.slice(-limit) : text.slice(0, limit), length: text.length };
    }
    const walker = document.createTreeWalker(el, 4);
    let node = tail ? walker.lastChild() : walker.nextNode();
    const parts = [];
    let length = 0;
    let sampled = 0;
    let visited = 0;
    while (node && sampled < limit && visited++ < 64) {
      const parent = node.parentElement;
      const closedDetails = parent?.closest('details:not([open])');
      const hiddenByDetails = closedDetails && parent?.closest('summary')?.parentElement !== closedDetails;
      const hiddenByAttributes = parent?.closest('script, style, noscript, template, [hidden], [aria-hidden="true"]');
      let visibilityParent = parent;
      let hiddenByStyle = Boolean(hiddenByDetails || hiddenByAttributes);
      // display:contents has visible descendant text but no box of its own.
      for (let depth = 0; !hiddenByStyle && typeof visibilityParent?.checkVisibility === 'function' && depth < 16; depth++) {
        if (visibilityParent.checkVisibility({ visibilityProperty: true })) break;
        const style = window.getComputedStyle(visibilityParent);
        if (style.display !== 'contents' || style.visibility !== 'visible') { hiddenByStyle = true; break; }
        visibilityParent = visibilityParent.parentElement;
      }
      if (!hiddenByStyle) {
        const value = node.data || '';
        length += value.length;
        const part = tail ? value.slice(-Math.max(0, limit - sampled)) : value.slice(0, limit - sampled);
        if (tail) parts.unshift(part); else parts.push(part);
        sampled += part.length;
      }
      node = tail ? walker.previousNode() : walker.nextNode();
    }
    return { text: parts.join(' ').replace(/\s+/g, ' ').trim(), length };
  };

  const textOf = (el) => textSampleOf(el).text;

  const labelOf = (el) => {
    return [
      el?.getAttribute?.('aria-label'),
      el?.getAttribute?.('title'),
      textOf(el)
    ].filter(Boolean).join(' ').trim();
  };

  const isEditableElement = (el) => {
    if (!el) return false;
    if (el instanceof HTMLTextAreaElement) return true;
    if (el instanceof HTMLInputElement) {
      const type = (el.type || 'text').toLowerCase();
      return !['button', 'checkbox', 'color', 'file', 'hidden', 'image', 'radio', 'range', 'reset', 'submit'].includes(type);
    }

    const role = el.getAttribute?.('role') || '';
    return el.isContentEditable || role === 'textbox';
  };

  const compactText = (value) => (value == null ? '' : String(value)).replace(/\s+/g, ' ').trim();

  const readScalarText = (value) => {
    if (value == null) return '';
    return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'
      ? compactText(value)
      : '';
  };

  const readPath = (target, path) => {
    return path.reduce((current, key) => current == null ? undefined : current[key], target);
  };

  const readFirstPath = (target, paths) => {
    if (!target || typeof target !== 'object') return '';
    for (const path of paths) {
      const text = readScalarText(readPath(target, path));
      if (text) return text;
    }

    return '';
  };

  const uniqueObjects = (items) => {
    const seen = new Set();
    return items.filter((item) => {
      if (!item || typeof item !== 'object' || seen.has(item)) return false;
      seen.add(item);
      return true;
    });
  };

  return {
    isVisible,
    textOf,
    textSampleOf,
    labelOf,
    isEditableElement,
    compactText,
    readScalarText,
    readPath,
    readFirstPath,
    uniqueObjects
  };
})();
