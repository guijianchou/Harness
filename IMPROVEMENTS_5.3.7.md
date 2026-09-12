# Harness 5.3.7 Improvements Summary

## Version 5.3.7 Changes

### 1. Unified N/A Fallback Display

**Issue**: When status information is unavailable or detection fails, different status controls displayed inconsistent fallback values (blank text, default placeholders, or mixed behavior).

**Solution**: Standardized all status presenter methods to return "N/A" when information is unavailable, providing consistent visual feedback across the entire status bar.

**Modified Methods in [StatusPresenter.cs](src/Harness/ViewModels/StatusPresenter.cs)**:

1. **FormatModelSummary()** (line 60)
   ```csharp
   if (string.IsNullOrWhiteSpace(model))
   {
       return "N/A";  // Was: return defaultModelSummary;
   }
   ```

2. **FormatHeartbeatSummary()** (line 22)
   ```csharp
   _ => new StatusPresentation("N/A", brushes.Warning),  // Was: defaultHeartbeatSummary
   ```

3. **FormatLatencySummary()** (line 37)
   ```csharp
   return new StatusPresentation("N/A", brushes.Neutral);  // Was: defaultLatencySummary
   ```

4. **FormatAccessSummary()** (line 79)
   ```csharp
   _ => new StatusPresentation("N/A", brushes.Warning),  // Was: defaultAccessSummary
   ```

5. **FormatWorkStatus()** (line 96)
   ```csharp
   return new WorkStatusPresentation("N/A", brushes.Warning, RunIndicatorMode.Wait);  // Was: defaultWorkStatus
   ```

**Impact**: 
- Clear, consistent feedback when any status component fails to retrieve information
- Users immediately understand that detection failed rather than seeing confusing blank space
- Improved user experience across all status bar elements (Model, Heartbeat, Latency, Access, Work)

---

### 2. Enhanced OpenClaw Model Detection

**Issue**: OpenClaw model detection relies heavily on a single CSS class (`.chat-controls__inline-select-label`) which may be too limited and fragile to UI changes.

**Solution**: Expanded DOM scanning with multiple fallback patterns while maintaining the source code reading approach:

#### 2.1 Direct Property Reading
Added support for reading model information directly from `<openclaw-app>` element properties if exposed:

```javascript
const readOpenClawAppStateModel = () => {
  const app = appStateElement ? document.querySelector(appStateElement) : null;
  if (app) {
    // Priority 1: Direct properties (if OpenClaw exposes them)
    const directModel = dom.compactText(app.currentModel || app.modelId || '');
    if (directModel) {
      const directProvider = dom.compactText(app.modelProvider || app.provider || '');
      const formatted = directProvider ? `${directProvider}/${directModel}` : directModel;
      return modelResult(formatted, 'app-state:property');
    }
  }
  
  // Priority 2: Nested state resolution (existing approach)
  // Priority 3: DOM fallback
};
```

**Properties checked**:
- `app.currentModel` - current model name
- `app.modelId` - model ID
- `app.modelProvider` - model provider
- `app.provider` - provider fallback

#### 2.2 Additional DOM Selectors
Enhanced `readModelFromDomCandidates()` with layered OpenClaw-specific patterns:

**Priority 1a** (score 140): `.chat-controls__inline-select-label` (existing)

**Priority 1b** (score 138): Class pattern matching
```javascript
[class*="model-select"], [class*="model-picker"], [class*="chat-model"]
```
Only accepts if text contains model-like patterns.

**Priority 1c** (score 136): ARIA label extraction
```javascript
button[aria-label*="model"], [role="button"][aria-label*="model"]
```
Extracts model from aria-labels like `"Current model: openclaw/claude-opus-5"`

**Priority 1d** (score 134): Model button text
Buttons with model-related aria-labels that contain model patterns in their text content.

**Detection Hierarchy**:
1. Direct `<openclaw-app>` properties (most reliable)
2. Nested state object resolution (existing robust approach)
3. Primary CSS class `.chat-controls__inline-select-label`
4. Generic model-related class patterns
5. ARIA label extraction
6. Button text with model patterns
7. DeepSeek-specific patterns (score 135)
8. Generic data attributes (score 120)
9. Select elements (score 90-115)
10. Generic controls (score 80-105)

---

### 3. Build Configuration

**No changes to build output structure in this version** - builds continue to output to:
```
bin/5.3.7/{Configuration}/{TargetFramework}/{RuntimeIdentifier}/
```

Example paths:
- Debug: `bin/5.3.7/Debug/net10.0-windows10.0.26100.0/win-x64/`
- Release: `bin/5.3.7/Release/net10.0-windows10.0.26100.0/win-x64/`

**Build commands**:
```bash
# From src/Harness directory
cd src/Harness

# Debug build
dotnet build -c Debug -r win-x64

# Release build  
dotnet build -c Release -r win-x64
```

---

## Documentation

### New Files
1. **[OPENCLAW_MODEL_DETECTION.md](OPENCLAW_MODEL_DETECTION.md)** - Comprehensive guide on OpenClaw model detection approach, limitations, and recommended enhancements for OpenClaw to expose direct properties.

2. **[IMPROVEMENTS_5.3.7.md](IMPROVEMENTS_5.3.7.md)** - This document summarizing all changes in version 5.3.7.

---

## Testing Recommendations

### Status Bar N/A Display Testing
1. **With unavailable backend**:
   - Verify all status indicators show "N/A" when no backend is connected
   - Test Model, Heartbeat, Latency, Access, and Work Status individually

2. **During transitions**:
   - Check N/A appears during initial load before information becomes available
   - Verify smooth transitions from N/A to actual values when detection succeeds

3. **With partial failures**:
   - Test when some status components succeed while others fail
   - Verify each component independently shows N/A when appropriate

### Model Detection Testing
1. **With OpenClaw**: 
   - Verify model name appears in status bar
   - Change models and verify status bar updates
   - Test with different model formats (openclaw/model-name, provider/model-name)
   - Check N/A appears during initial load before model is detected

2. **Without Model Info**:
   - Verify "N/A" displays instead of blank space
   - Test with backend that doesn't expose model information

3. **Multiple Selectors**:
   - Test with OpenClaw UIs that use different CSS class names
   - Verify fallback cascade works when primary selector doesn't match
   - Check ARIA label extraction with different label formats

### Edge Cases
- Empty model strings should show "N/A"
- Whitespace-only model strings should show "N/A"
- Model changes during active session should update status bar
- Effort levels should still append correctly: `model-name (medium)`
- Status bar should handle rapid backend switches gracefully
- All N/A displays should use appropriate brush colors (Warning/Error/Neutral)

---

## Modified Files

### C# Files
- `src/Harness/ViewModels/StatusPresenter.cs` - Unified N/A fallback logic across all status methods
- `src/Harness/Harness.csproj` - Version updated to 5.3.7

### JavaScript Files
- `src/Harness/Services/HostedUiBridge.ModelDomFallback.js`:
  - Enhanced `readOpenClawAppStateModel()` with direct property reading
  - Expanded `readModelFromDomCandidates()` with multiple OpenClaw selector patterns

### Documentation Files
- `README.md` - Updated to version 5.3.7 with feature summary
- `readme_zh.md` - Updated to version 5.3.7 with Chinese feature summary

---

## Future Enhancements

### Recommended OpenClaw Changes
For optimal reliability, OpenClaw should expose model information as direct properties on `<openclaw-app>`:

```javascript
<openclaw-app 
  connected="true"
  current-model="openclaw/claude-opus-5"
  current-effort="medium"
  model-provider="openclaw"
  model-id="claude-opus-5"
  ...other properties...
>
```

This would eliminate dependency on CSS classes and DOM structure, providing the most reliable detection method.

See [OPENCLAW_MODEL_DETECTION.md](OPENCLAW_MODEL_DETECTION.md) for detailed recommendations.

---

## Build Verification

Both Debug and Release configurations build successfully:
- ✅ Debug: `bin/5.3.7/Debug/net10.0-windows10.0.26100.0/win-x64/Harness.dll`
- ✅ Release: `bin/5.3.7/Release/net10.0-windows10.0.26100.0/win-x64/Harness.dll`

No build warnings or errors.

