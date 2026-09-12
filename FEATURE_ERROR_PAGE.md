# Connection Error Page

Updated for Harness 5.3.6.

## Behavior

Navigation failures show an embedded local recovery page after automatic retries finish, or immediately when Auto refresh is disabled. Certificate failures retain AuthFailed; other failures retain Error. Loading this local page does not signal a recovered hosted session or start hosted status probes.

The page displays the actual Gateway URL and WebView2 error status. Retry navigates to the last requested Gateway. The native Reload command uses the same retry path while the local error page is active. Settings raises ErrorPageSettingsRequested, which the shell routes to the Settings dialog.

## Presentation

- English and Chinese text comes from the existing Resources.resw files.
- Light and dark colors follow the WebView control's ActualTheme.
- The app PNG icon is embedded as a data URL.
- Recovery actions precede technical details and remain visible in small viewports.
- Long URLs wrap without horizontal overflow.

## Ownership And Data

WebViewService.ErrorPage.cs serializes all page data with System.Text.Json into the single __HARNESS_ERROR_DATA__ placeholder. The page assigns labels and diagnostics with textContent.

Actions use the harness-error-page-action message type and carry an errorPageToken. The native handler accepts them only when the message and active document both have the about:blank source and the token matches the current local page. Real navigation or host detachment clears that ownership.

NavigateToString reports a data:text/html navigation-start URI in the tested WebView2 runtime and an about:blank completed source. The navigation handlers recognize this local navigation without publishing a successful Gateway connection.

Navigation diagnostics pass through the shared log redactor before they are written to disk.

## Verification

Build Debug x64 first, then run:

```powershell
pwsh -NoProfile -STA -File tools/verify-native-webview.ps1 -OutputDirectory "$env:TEMP/Harness-error-page-checks"
```

The verifier uses current production source with a standalone native WebView2 controller and a temporary browser profile. Gateway requests are intercepted with local HTML. It checks error data and script injection, Retry and Reload routing, action ownership, failure-state preservation, and both locales/themes at 900x600, 480x640, and 320x480. PNG previews are written to the requested directory.

WinUI presentation and coordinator dependencies are stubbed in this verifier. It does not establish end-to-end connectivity to a real Gateway or full shell acceptance.

## Source

- src/Harness/Assets/ErrorPages/connection-error.html
- src/Harness/Services/WebViewService.ErrorPage.cs
- src/Harness/Services/WebViewService.Navigation.cs
- src/Harness/Services/WebViewService.HostMessages.cs
- tools/NativeWebViewRegression.cs
