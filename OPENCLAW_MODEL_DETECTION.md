# OpenClaw Model Detection Enhancement

## Current State

The Harness currently reads OpenClaw model information through multiple fallback approaches:

1. **App State Resolution** (`readOpenClawAppStateModel`): Reads from nested state objects within the `<openclaw-app>` custom element, looking for session-specific model overrides and defaults
2. **Model Select Element** (`readOpenClawModelSelect`): Looks for `<select data-chat-model-select>` elements
3. **DOM Scanning** (`readModelFromDomCandidates`): Scans for model indicators in the DOM with priority scoring:
   - Priority 1: `.chat-controls__inline-select-label` (OpenClaw specific, score 140)
   - Priority 2: Trigger labels in model selection buttons (DeepSeek specific, score 135)
   - Priority 3: Generic data attributes and controls (scores 80-120)

## Limitations

While the DOM scanning approach works, it has limitations:
- CSS class names may change between OpenClaw versions
- DOM structure changes can break detection
- Priority scoring may not capture all UI variations
- Timing issues if model UI hasn't rendered when inspection runs

## Recommended Enhancement

### Direct Property Exposure

The most reliable approach would be for OpenClaw to expose model and effort information directly as properties on the `<openclaw-app>` custom element, similar to how it currently exposes activity state:

```javascript
// Current properties already exposed by <openclaw-app>:
app.connected         // boolean - connection status
app.tab              // string - active tab name
app.chatLoading      // boolean - chat loading state
app.chatSending      // boolean - chat sending state
app.chatRunId        // string - active run ID
// ... and many other activity properties

// Proposed additions for model detection:
app.currentModel     // string - current model name (e.g., "openclaw/claude-opus-5")
app.currentEffort    // string - reasoning effort level (e.g., "medium")
app.modelProvider    // string - model provider (e.g., "openclaw")
app.modelId          // string - model ID without provider prefix
```

### Benefits

1. **Reliability**: Direct property access is immune to CSS class and DOM structure changes
2. **Performance**: No DOM scanning required, instant property access
3. **Accuracy**: Source of truth comes from OpenClaw's internal state
4. **Simplicity**: Single property read instead of multi-stage fallback

### Implementation in Harness

If OpenClaw exposes these properties, the reading code would be updated to prioritize them:

```javascript
const readOpenClawAppStateModel = () => {
  const app = appStateElement ? document.querySelector(appStateElement) : null;
  if (!app) return emptyModelResult();
  
  // Priority 1: Direct property (NEW)
  const directModel = dom.compactText(app.currentModel || '');
  if (directModel) {
    return modelResult(directModel, 'app-state:property');
  }
  
  // Priority 2: Nested state resolution (EXISTING)
  const states = readOpenClawStateCandidates();
  const stateModel = modelResolver.resolveOpenClawAppStateModel(states, readCurrentSessionKey(states));
  if (stateModel?.value) return stateModel;
  
  // Priority 3: DOM fallback (EXISTING)
  return emptyModelResult();
};
```

## Current Workaround

Until direct properties are exposed, the Harness relies on:
1. Deep state object inspection within `<openclaw-app>`
2. CSS class-based DOM scanning with OpenClaw-specific selectors
3. Generic fallback patterns for unknown providers

The detection includes OpenClaw-specific handling in the regex patterns and DOM selectors to maximize reliability with the current approach.

## Version History

- **v5.3.7**: Added N/A display when model reading fails, ensuring status bar always shows meaningful information
- **v5.3.6**: Added full OpenClaw and DeepSeek model detection support with provider/model format
