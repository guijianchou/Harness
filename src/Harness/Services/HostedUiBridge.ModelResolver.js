const openClawModelResolver = (() => {
  const compactText = (value) => (value == null ? '' : String(value)).replace(/\s+/g, ' ').trim();
  const collapseRepeatedModelLabel = (value) => {
    const normalized = compactText(value);
    const providerMatch = normalized.match(/^([^/\s]+\/)(.+)$/);
    const providerPrefix = providerMatch ? providerMatch[1] : '';
    const model = providerMatch ? providerMatch[2] : normalized;
    const hyphenRepeated = model.match(/^([a-z][a-z0-9]*(?:-[a-z0-9.]+)+)-\1$/i);
    if (hyphenRepeated) return `${providerPrefix}${hyphenRepeated[1]}`;

    const whitespaceRepeated = model.match(/^(.+?)\s+\1$/i);
    return whitespaceRepeated ? `${providerPrefix}${whitespaceRepeated[1]}` : normalized;
  };
  const cleanTrustedModelValue = (value) => collapseRepeatedModelLabel(value).slice(0, 96);
  const readScalarText = (value) => {
    if (value == null) return '';
    return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean'
      ? compactText(value)
      : '';
  };

  const emptyModelResult = () => ({ value: '', source: '' });
  const modelResult = (value, source) => {
    return { value: cleanTrustedModelValue(value), source };
  };
  const MODEL_FIELD_KEYS = ['model', 'modelOverride', 'selectedModel', 'chatModel', 'modelId'];
  const PROVIDER_FIELD_KEYS = ['modelProvider', 'providerOverride', 'provider', 'modelProviderOverride'];
  const OVERRIDE_MODEL_FIELD_KEYS = ['value', ...MODEL_FIELD_KEYS];
  const MODEL_OBJECT_FIELD_KEYS = ['id', 'value', 'name', ...MODEL_FIELD_KEYS];
  const PROVIDER_OBJECT_FIELD_KEYS = ['id', 'value', 'name', ...PROVIDER_FIELD_KEYS];
  const SESSION_ID_KEYS = ['key', 'sessionKey', 'id'];
  const SESSIONS_RESULT_PATHS = [
    ['sessionsResult'],
    ['chatSessionsResult'],
    ['sessionResult']
  ];
  const MODEL_CATALOG_PATHS = [
    ['chatModelCatalog'],
    ['modelCatalog'],
    ['models']
  ];
  const MODEL_OVERRIDES_PATHS = [
    ['chatModelOverrides'],
    ['modelOverrides'],
    ['settings', 'chatModelOverrides']
  ];

  const readPath = (target, path) => {
    return path.reduce((current, key) => current == null ? undefined : current[key], target);
  };

  const readFirstKey = (target, keys) => {
    if (!target || typeof target !== 'object') return '';
    for (const key of keys) {
      const text = readScalarText(target[key]);
      if (text) return text;
    }

    return '';
  };

  const readModelLikeValue = (target, keys, objectKeys = keys) => {
    if (!target || typeof target !== 'object') return '';
    for (const key of keys) {
      const value = target[key];
      const scalar = readScalarText(value);
      if (scalar) return scalar;

      if (value && typeof value === 'object') {
        const nested = readFirstKey(value, objectKeys);
        if (nested) return nested;
      }
    }

    return '';
  };

  const readFirstObjectPath = (target, paths) => {
    if (!target || typeof target !== 'object') return null;
    for (const path of paths) {
      const value = readPath(target, path);
      if (value && typeof value === 'object') return value;
    }

    return null;
  };

  const readFirstArrayPath = (target, paths) => {
    if (!target || typeof target !== 'object') return [];
    for (const path of paths) {
      const value = readPath(target, path);
      if (Array.isArray(value)) return value;
    }

    return [];
  };

  const resolveCatalogModelValue = (model, catalog) => {
    const cleanModel = cleanTrustedModelValue(model);
    if (!cleanModel || cleanModel.includes('/') || !Array.isArray(catalog)) return cleanModel;

    const matches = catalog
      .filter((entry) => compactText(entry?.id).toLowerCase() === cleanModel.toLowerCase())
      .map((entry) => formatModelValue(entry.id, entry.provider, []))
      .filter(Boolean);
    const uniqueMatches = Array.from(new Set(matches.map((entry) => entry.toLowerCase())));
    return uniqueMatches.length === 1 ? matches[0] : cleanModel;
  };

  const formatModelValue = (model, provider, catalog = []) => {
    const cleanModel = cleanTrustedModelValue(model);
    const cleanProvider = cleanTrustedModelValue(provider);
    if (!cleanModel) return '';

    // If no provider or model already has provider prefix, resolve via catalog
    if (!cleanProvider || cleanModel.includes('/')) return resolveCatalogModelValue(cleanModel, catalog);

    // For openclaw and deepseek: full provider/model format support
    const providerLower = cleanProvider.toLowerCase();
    const modelLower = cleanModel.toLowerCase();
    const providerPrefix = `${providerLower}/`;

    // Special handling for known providers
    if (providerLower === 'openclaw' || providerLower === 'deepseek') {
      // If model already starts with provider, return as-is
      if (modelLower.startsWith(providerPrefix)) return cleanModel;
      // Otherwise format as provider/model
      return `${cleanProvider}/${cleanModel}`;
    }

    // Generic provider: try to format nicely, but be permissive
    if (modelLower.startsWith(providerPrefix)) {
      return cleanModel;
    }

    // For generic providers, add prefix only if it looks intentional
    // (avoid turning "gpt-4" into "openai/gpt-4" if provider comes from somewhere unexpected)
    return `${cleanProvider}/${cleanModel}`;
  };

  const readServerModelValue = (entry, catalog = []) => {
    if (!entry || typeof entry !== 'object') return '';
    return formatModelValue(
      readModelLikeValue(entry, MODEL_FIELD_KEYS, MODEL_OBJECT_FIELD_KEYS),
      readModelLikeValue(entry, PROVIDER_FIELD_KEYS, PROVIDER_OBJECT_FIELD_KEYS),
      catalog);
  };

  const readOverrideModelValue = (override, catalog = []) => {
    if (!override) return '';
    if (typeof override === 'string') return resolveCatalogModelValue(override, catalog);
    if (typeof override !== 'object') return '';
    return formatModelValue(
      readModelLikeValue(override, OVERRIDE_MODEL_FIELD_KEYS, MODEL_OBJECT_FIELD_KEYS),
      readModelLikeValue(override, PROVIDER_FIELD_KEYS, PROVIDER_OBJECT_FIELD_KEYS),
      catalog);
  };

  const hasOverrideForSession = (overrides, sessionKey) => {
    if (!sessionKey || !overrides || typeof overrides !== 'object') return false;
    return overrides instanceof Map
      ? overrides.has(sessionKey)
      : Object.prototype.hasOwnProperty.call(overrides, sessionKey);
  };

  const readOverrideForSession = (overrides, sessionKey) => {
    return overrides instanceof Map ? overrides.get(sessionKey) : overrides[sessionKey];
  };

  const readSessionIdentifier = (session) => readFirstKey(session, SESSION_ID_KEYS);

  const resolveOpenClawAppStateModel = (states, sessionKey) => {
    if (!Array.isArray(states) || states.length === 0) return emptyModelResult();

    const currentSessionKey = compactText(sessionKey);
    let firstDefaultModel = '';
    let nullOverrideDefaultModel = '';
    for (const state of states) {
      const sessionsResult = readFirstObjectPath(state, SESSIONS_RESULT_PATHS);
      const chatModelCatalog = readFirstArrayPath(state, MODEL_CATALOG_PATHS);
      const defaultsModel = readServerModelValue(sessionsResult?.defaults, chatModelCatalog);
      const overrides = readFirstObjectPath(state, MODEL_OVERRIDES_PATHS) || {};

      if (!firstDefaultModel && defaultsModel) {
        firstDefaultModel = defaultsModel;
      }

      if (hasOverrideForSession(overrides, currentSessionKey)) {
        const override = readOverrideForSession(overrides, currentSessionKey);
        if (override === null) {
          if (defaultsModel) {
            nullOverrideDefaultModel = defaultsModel;
          }
        } else {
          const overrideModel = readOverrideModelValue(override, chatModelCatalog);
          if (overrideModel) {
            return modelResult(overrideModel, 'app-state:override');
          }
        }
      }

      const sessions = Array.isArray(sessionsResult?.sessions) ? sessionsResult.sessions : [];
      const activeSession = sessions.find((row) => compactText(readSessionIdentifier(row)) === currentSessionKey);
      const activeModel = readServerModelValue(activeSession, chatModelCatalog);
      if (activeModel) {
        return modelResult(activeModel, 'app-state:session');
      }
    }

    if (nullOverrideDefaultModel) {
      return modelResult(nullOverrideDefaultModel, 'app-state:default');
    }

    return firstDefaultModel
      ? modelResult(firstDefaultModel, 'app-state:default')
      : emptyModelResult();
  };

  return { resolveOpenClawAppStateModel };
})();

const resolveOpenClawAppStateModel = openClawModelResolver.resolveOpenClawAppStateModel;
