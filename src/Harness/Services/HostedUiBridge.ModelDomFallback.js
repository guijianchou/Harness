const openClawModelDomFallback = (() => {
  // Primary providers: openclaw and deepseek (full support)
  // Generic fallback: common LLM keywords for other providers
  const modelPattern = /\b(?:openclaw|deepseek|gpt|o\d|claude|gemini|qwen|llama|mistral|glm|yi|command|grok|codex|kimi|moonshot)[a-z0-9._:+/-]*\b/i;

  // Relaxed pattern for generic provider fallback - matches common model naming patterns
  const genericModelPattern = /(?:[a-z]+-)?(?:chat|turbo|vision|reasoning|pro|lite|mini|plus|ultra|max|preview|experimental|instruct|code|base|small|medium|large|xl|xxl)(?:-[a-z0-9]+)?/i;

  const createReader = ({ dom, mutationFilter, modelResolver, profile }) => {
    const appStateElement = profile?.appStateElement || '';
    const isStatusProbeExcludedElement = mutationFilter.isStatusProbeExcludedElement;

    const collapseRepeatedModelLabel = (value) => {
      const normalized = dom.compactText(value);
      const providerMatch = normalized.match(/^([^/\s]+\/)(.+)$/);
      const providerPrefix = providerMatch ? providerMatch[1] : '';
      const model = providerMatch ? providerMatch[2] : normalized;
      const hyphenRepeated = model.match(/^([a-z][a-z0-9]*(?:-[a-z0-9.]+)+)-\1$/i);
      if (hyphenRepeated) return `${providerPrefix}${hyphenRepeated[1]}`;

      const whitespaceRepeated = model.match(/^(.+?)\s+\1$/i);
      return whitespaceRepeated ? `${providerPrefix}${whitespaceRepeated[1]}` : normalized;
    };

    const sanitizeModelLabel = (text) => {
      let normalized = dom.compactText(text)
        .replace(/\b(?:current|selected|default)\s+model\b[:\s-]*/ig, '')
        .replace(/\bmodel\b[:\s-]*/ig, '')
        .replace(/\bprovider\b[:\s-]*/ig, '')
        .replace(/\s+\|\s+/g, ' | ')
        .trim();

      if (!normalized) return '';

      normalized = collapseRepeatedModelLabel(normalized);

      // Blacklist: filter out obvious UI prose that should never be model names
      // Match multi-word phrases that are clearly UI messages
      if (normalized.split(/\s+/).length >= 3) {
        const uiPhrasePatterns = [
          /\b(?:ran|run|running|executed?)\s+\d+\s+commands?\b/i,
          /\b(?:think|thought|thinking)\s+(?:the\s+)?user\s+wants?\b/i,
          /\b(?:loading|waiting|processing|generating|analyzing)\b/i,
        ];
        if (uiPhrasePatterns.some(pattern => pattern.test(normalized))) {
          return '';
        }
      }

      const defaultWrappedModel = normalized.match(/^(?:default|selected|current)(?:\s+model)?\s*\(([^)]+)\)$/i);
      if (defaultWrappedModel) {
        return sanitizeModelLabel(defaultWrappedModel[1]);
      }

      const prefixedModel = normalized
        .replace(/^(?:default|selected|current)(?:\s+model)?\s*[:\s-]+/i, '')
        .replace(/^\(([^)]+)\)$/, '$1')
        .trim();
      if (prefixedModel && prefixedModel !== normalized) {
        return sanitizeModelLabel(prefixedModel);
      }

      // Try primary pattern first (openclaw, deepseek, common LLMs)
      if (normalized.length <= 32 && modelPattern.test(normalized)) return normalized;

      // Try to find a matching segment
      const segment = normalized
        .split(/(?:\s{3,}|\n|\||,)/)
        .map((part) => dom.compactText(part))
        .find((part) => modelPattern.test(part));

      if (segment) return segment.length <= 32 ? segment : segment.slice(0, 31).trimEnd();

      const match = normalized.match(modelPattern);
      if (match) return match[0];

      // Generic fallback: if normalized looks like a model name (contains typical model patterns)
      // or is short enough and doesn't look like UI text, accept it
      if (normalized.length <= 32) {
        // If it matches generic model naming patterns, accept it
        if (genericModelPattern.test(normalized)) return normalized;

        // If it looks like provider/model format, accept it
        if (/^[a-z0-9-]+\/[a-z0-9._-]+$/i.test(normalized)) return normalized;

        // If it's short and alphanumeric-ish (not prose), accept it as fallback
        if (normalized.length >= 3 && normalized.length <= 32 && /^[a-z0-9._:\/-]+$/i.test(normalized)) {
          return normalized;
        }
      }

      return '';
    };

    const cleanTrustedModelValue = (value) => collapseRepeatedModelLabel(value).slice(0, 96);
    const emptyModelResult = () => ({ value: '', source: '' });
    const modelResult = (value, source) => {
      return { value: cleanTrustedModelValue(value), source };
    };
    const SESSION_KEY_PATHS = [
      ['sessionKey'],
      ['activeSessionKey'],
      ['currentSessionKey'],
      ['chatSessionKey'],
      ['session', 'key'],
      ['settings', 'sessionKey']
    ];
    const APP_STATE_PATHS = [
      ['state'],
      ['appState'],
      ['store', 'state'],
      ['controller', 'state'],
      ['chatState'],
      ['sessionState']
    ];

    const readSessionKeyFromUrl = () => {
      try {
        return dom.compactText(new URL(window.location.href).searchParams.get('session') || '');
      } catch {
        return '';
      }
    };

    const readOpenClawStateCandidates = () => {
      const app = appStateElement ? document.querySelector(appStateElement) : null;
      if (!app) return [];
      return dom.uniqueObjects([
        app,
        ...APP_STATE_PATHS
          .map((path) => dom.readPath(app, path))
          .filter((value) => value && typeof value === 'object')
      ]);
    };

    const readCurrentSessionKey = (states) => {
      for (const state of states) {
        const key = dom.readFirstPath(state, SESSION_KEY_PATHS);
        if (key) return key;
      }

      return readSessionKeyFromUrl();
    };

    const readOpenClawAppStateModel = () => {
      // Check for direct properties first (if OpenClaw exposes them)
      const app = appStateElement ? document.querySelector(appStateElement) : null;
      if (app) {
        // Try reading direct model property
        const directModel = dom.compactText(app.currentModel || app.modelId || '');
        if (directModel) {
          const directProvider = dom.compactText(app.modelProvider || app.provider || '');
          const formatted = directProvider ? `${directProvider}/${directModel}` : directModel;
          return modelResult(formatted, 'app-state:property');
        }
      }

      // Fallback to nested state resolution
      const states = readOpenClawStateCandidates();
      if (states.length === 0) return emptyModelResult();

      return modelResolver.resolveOpenClawAppStateModel(states, readCurrentSessionKey(states));
    };

    const readOpenClawModelSelect = () => {
      const select = document.querySelector('select[data-chat-model-select="true"], select[data-chat-model-select]');
      if (!(select instanceof HTMLSelectElement) || !dom.isVisible(select)) return emptyModelResult();

      const selectedOption = select.selectedOptions?.[0] || null;
      const selectedModelOptionValue = dom.compactText(selectedOption?.value || '');
      const selectedModelValue = dom.compactText(select.value || '');
      const selectedModelTitle = dom.compactText(select.getAttribute('title') || '');
      const selectedModelText = dom.compactText(selectedOption?.textContent || '');

      for (const value of [
        selectedModelOptionValue,
        selectedModelValue,
        selectedModelTitle,
        selectedModelText
      ]) {
        const label = sanitizeModelLabel(value);
        if (label) return modelResult(label, 'model-select');
      }

      return emptyModelResult();
    };

    const readModelFromDomCandidates = () => {
      const candidates = [];
      const selectionBoostOf = (el) => {
        const selected = [
          el?.getAttribute?.('aria-selected'),
          el?.getAttribute?.('aria-checked'),
          el?.getAttribute?.('aria-pressed'),
          el?.getAttribute?.('data-selected'),
          el?.getAttribute?.('data-state')
        ].filter(Boolean).join(' ').toLowerCase();
        return /true|selected|checked|active|current/.test(selected) ? 18 : 0;
      };

      const pushCandidate = (text, score, el, source) => {
        const label = sanitizeModelLabel(text);
        if (!label) return;
        candidates.push({ label, source, score: score + selectionBoostOf(el) });
      };
      const bestCandidate = () => {
        if (candidates.length === 0) return emptyModelResult();
        candidates.sort((left, right) => right.score - left.score || left.label.length - right.label.length);
        return modelResult(candidates[0].label, candidates[0].source);
      };

      // Priority 1: OpenClaw's model label must stay inside the model summary.
      // The effort picker uses the same label class, so scanning that class
      // globally can mistake "High" for the model.
      Array.from(document.querySelectorAll(
        'summary[data-model-select="true"] .chat-controls__inline-select-label, ' +
        '.chat-controls__model-picker .chat-controls__inline-select-label'))
        .filter((el) => !isStatusProbeExcludedElement(el) && dom.isVisible(el))
        .forEach((el) => pushCandidate(dom.textOf(el), 150, el, 'dom:openclaw-model-label'));
      if (candidates.length > 0) return bestCandidate();

      // Priority 2: DeepSeek's model control is the button whose accessible
      // name contains "Select model". Other triggerLabel elements, such as
      // Settings, are unrelated controls and must not be considered.
      Array.from(document.querySelectorAll('button[aria-label], [role="button"][aria-label]'))
        .filter((el) => /\bselect\s+model\b/i.test(el.getAttribute('aria-label') || '') &&
          !isStatusProbeExcludedElement(el) && dom.isVisible(el))
        .forEach((el) => {
          const ariaLabel = el.getAttribute('aria-label') || '';
          if (!/\bselect\s+model\b/i.test(ariaLabel)) return;

          const currentModelMatch = ariaLabel.match(/\bcurrent\s+(.+?)(?=\s*,\s*reasoning\s+effort\b|$)/i);
          if (currentModelMatch) {
            pushCandidate(currentModelMatch[1], 148, el, 'dom:deepseek-model-aria');
            return;
          }

          const label = el.querySelector?.('[class*="triggerLabel"], [class*="trigger-label"]');
          if (label && !isStatusProbeExcludedElement(label) && dom.isVisible(label)) {
            pushCandidate(dom.textOf(label), 145, label, 'dom:deepseek-model-label');
            return;
          }

          const title = el.getAttribute('title') || '';
          const titleModel = title.split(/\s*[·|]\s*/, 1)[0];
          if (titleModel) pushCandidate(titleModel, 142, el, 'dom:deepseek-model-title');
        });
      if (candidates.length > 0) return bestCandidate();

      // Priority 3: Generic data attributes
      Array.from(document.querySelectorAll('[data-current-model], [data-selected-model], [data-model-name]'))
        .filter((el) => !isStatusProbeExcludedElement(el) && dom.isVisible(el))
        .forEach((el) => pushCandidate(dom.textOf(el), 120, el, 'dom:data-model'));
      if (candidates.length > 0) return bestCandidate();

      Array.from(document.querySelectorAll('select'))
        .filter((el) => !isStatusProbeExcludedElement(el) && dom.isVisible(el))
        .forEach((el) => {
          const selectedText = Array.from(el.selectedOptions || [])
            .map((option) => option.textContent || '')
            .join(' ');
          const combined = `${dom.labelOf(el)} ${selectedText}`.trim();
          if (/\bmodel\b/i.test(combined) || modelPattern.test(selectedText)) {
            pushCandidate(selectedText || combined, /\bmodel\b/i.test(combined) ? 115 : 90, el, 'dom:select');
          }
        });

      Array.from(document.querySelectorAll('[role="combobox"], button[aria-haspopup="listbox"], button, [role="button"], input[type="text"], input:not([type])'))
        .filter((el) => !isStatusProbeExcludedElement(el))
        .forEach((el) => {
          const rawValue = 'value' in el && typeof el.value === 'string' ? el.value : '';
          const text = dom.textOf(el);
          const combined = [el.getAttribute?.('aria-label'), el.getAttribute?.('title'), text, rawValue, el.getAttribute?.('placeholder')].filter(Boolean).join(' ').trim();
          if ((!/\bmodel\b/i.test(combined) && !modelPattern.test(rawValue) && !modelPattern.test(text)) || !dom.isVisible(el)) return;
          const score = /\bmodel\b/i.test(combined) ? 105 : 80;
          pushCandidate(rawValue || text || combined, score, el, 'dom:control');
        });

      return bestCandidate();
    };

    const MODEL_SOURCE_READERS = [
      readOpenClawAppStateModel,
      readOpenClawModelSelect,
      readModelFromDomCandidates
    ];

    const readCurrentModel = () => {
      for (const readModel of MODEL_SOURCE_READERS) {
        const result = readModel();
        if (result?.value) return result;
      }

      return emptyModelResult();
    };

    const readCurrentEffort = () => {
      // Try to read effort from DeepSeek Harness UI
      // Look for aria-label containing "reasoning effort"
      const buttons = Array.from(document.querySelectorAll('button[aria-label*="reasoning effort"], button[aria-label*="Reasoning effort"]'));
      for (const button of buttons) {
        if (!isStatusProbeExcludedElement(button) && dom.isVisible(button)) {
          const ariaLabel = button.getAttribute('aria-label') || '';
          const match = ariaLabel.match(/reasoning\s+effort\s+(\w+)/i);
          if (match) {
            return { value: match[1], source: 'dom:effort-label' };
          }
        }
      }

      // Try to read from data attributes
      const effortElements = Array.from(document.querySelectorAll('[data-effort], [data-reasoning-effort]'));
      for (const el of effortElements) {
        if (!isStatusProbeExcludedElement(el) && dom.isVisible(el)) {
          const effort = el.getAttribute('data-effort') || el.getAttribute('data-reasoning-effort');
          if (effort) {
            return { value: dom.compactText(effort), source: 'dom:data-effort' };
          }
        }
      }

      return { value: '', source: '' };
    };

    return {
      readCurrentModel,
      readCurrentEffort,
      readOpenClawAppStateModel,
      readOpenClawModelSelect,
      readModelFromDomCandidates,
      sanitizeModelLabel
    };
  };

  return { createReader };
})();
