# Harness

**Language:** English | [简体中文](readme_zh.md)

**Current version:** 5.3.12

Lightweight Windows-native shell for hosted agent Control UIs, built with WinUI 3 and WebView2.

Harness targets remote management of **Hermes Agent and OpenClaw Gateways on a VPS**. Gateway protocols determine the native features; see [remote management scope and official contracts](docs/gateway-management.md). OpenClaw has a partial native integration today; Hermes native Gateway integration is not yet implemented. Other sites, including DeepSeek, run as ordinary web apps.

| Backend | Status |
|---|---|
| OpenClaw Gateway | Hosted Web UI supported; optional native Gateway v4 controls require deployment testing |
| Hermes Agent | Gateway integration scope documented; current code only provides auxiliary web adaptation |
| Other web apps | Generic browser hosting; no agent-specific model detection or run commands |

Select the application type under Settings → Environments. Auto detect recognizes supported agent pages and otherwise uses ordinary web hosting. Existing DeepSeek profiles migrate to Generic without changing their URL or browser profile. See [Hermes and backend support](docs/hermes-integration.md) for supported interfaces and validation boundaries.

An optional OpenClaw Gateway v4 connection now adds native session browsing, empty-session creation, model selection, precise run cancellation, and run notifications while retaining the hosted web workspace. Enable it per environment in Settings. See [OpenClaw integration](docs/openclaw-integration.md) for authentication, Cloudflare Access, compatibility boundaries, and validation; the new native path still requires testing against your deployment.

---

## Why the rename

This project began as **OpenClaw Manager**, a WebView2 + WinUI 3 client written specifically for OpenClaw's Control UI. Window chrome, tray, global hotkey, per-environment session isolation, heartbeat, navigation recovery, diagnostics, and logging are shared hosted-UI shell features. Backend profiles, the page bridge, and the optional native OpenClaw Gateway connection provide product-specific behavior.

Renaming the app to **Harness** marked the shift from an OpenClaw client to a general hosted web workspace. Dedicated agent support now focuses on Hermes Agent and OpenClaw.

The rename shipped in 5.3.0. DeepSeek Harness previously passed a manual hosted Web UI test and remains usable as an ordinary web app. Version 5.3.8 added optional OpenClaw Gateway v4 controls alongside the hosted workspace.

### What changed in the 5.3.0 rename

| Area | Change |
|---|---|
| App name, executable, window title, tray, About | `OpenClaw` → `Harness` (`Harness.exe`) |
| Solution | `OpenClaw.sln` → [Harness.sln](Harness.sln) |
| Shell project | `src/OpenClaw/OpenClaw.csproj` → [src/Harness/Harness.csproj](src/Harness/Harness.csproj) |
| Shared library | `src/OpenClaw.Core/OpenClaw.Core.csproj` → [src/Harness.Core/Harness.Core.csproj](src/Harness.Core/Harness.Core.csproj) |
| Test project | `tests/OpenClaw.Core.Tests` → [tests/Harness.Core.Tests](tests/Harness.Core.Tests) |
| All namespaces | `OpenClaw.*` → `Harness.*` |
| Embedded bridge resources | `OpenClaw.Services.*.js` → `Harness.Services.*.js` |
| Host↔bridge message kinds | `openclaw-session-ready` / `-event-gap` / `-control-ui-status` → `harness-*` |
| Single-instance lock / pipe | `OpenClaw.Manager.SingleInstance` → `Harness.SingleInstance` |
| Data directory | `%LOCALAPPDATA%\OpenClaw\` → `%LOCALAPPDATA%\Harness\` |
| Log / diagnostic file names | `openclaw-<date>.log` → `harness-<date>.log`, `openclaw-diagnostics-*.zip` → `harness-diagnostics-*.zip` |

> **Upgrading from 5.2.0 or earlier:** the data directory moved and is **not** migrated automatically. Harness starts with a clean profile — gateway environments must be re-added in Settings, and hosted Control UI logins must be redone. The old `%LOCALAPPDATA%\OpenClaw\` folder is left untouched, so you can copy `settings.json` across by hand or delete it once you are set up.

### What deliberately did not change

| Area | Reason |
|---|---|
| Gateway wording, `openclaw devices ...` hints, `__openclaw__/a2ui/` probe path | These describe the OpenClaw server, not this client |
| `openclaw-app` DOM probing and the `readOpenClaw*` bridge helpers | They read OpenClaw's Control UI element and app state by name |
| `OPENCLAW_NODE` / `OPENCLAW_ALLOW_NODE_SKIP` build-script variables | Local developer setups already depend on them |

---

## Overview

This project keeps the existing hosted web experience, but wraps it in a native WinUI 3 window with:

- environment switching
- per-environment WebView2 session isolation
- optional native OpenClaw Gateway v4 session and model controls
- connection recovery and heartbeat monitoring
- diagnostics and structured logs
- native theme and window integration

It is best suited for users who:

- run a Hermes Agent web interface or OpenClaw Gateway locally or remotely
- access it through Cloudflare Tunnel or a reverse proxy
- want a lightweight Windows-native client instead of keeping a browser tab open

## Current 5.3.12 Notes

Release date: **2026-11-26**. This release adds Hermes web adaptation and font preferences, and strengthens profile isolation, stop ownership, Settings saves, tray exit, and portable-update recovery. See the [5.3.12 release notes](docs/releases/5.3.12.md).

- Reserve dedicated Agent behavior for Hermes Agent and OpenClaw. Other sites, including existing DeepSeek environments, use Generic web hosting. Hermes support covers the official `hermes webapp` interface; native Hermes Gateway management remains a documented design, not an implemented feature.
- Choose System UI or bundled Noto Sans SC and a 10–32 px base size in Settings > Appearance. Apply saved preferences to native controls and hosted content without downloading fonts; preserve icons, code fonts, and separate web zoom.
- Verify WebView2's effective profile folder before creating a controller; reject policy or environment overrides that would share another profile. Keep HTTP probes stateless and single-instance activation pipe names aligned with the Windows session scope.
- Keep Stop tied to the original page, session, and run, including delayed fallbacks and timeouts. Preserve the active environment through renames and stop later saves from overwriting settings already committed elsewhere.
- Keep the window accessible when the tray is unavailable and allow a second close within 15 seconds to exit. Prevent diagnostic exports from overwriting each other.
- Restart the restored version even if update-result reporting fails. Normalize only generated apphosts and the update's three staged EXEs to Medium integrity; preserve directory labels and DACLs. Include x86 testhost handling.

Real Hermes/OpenClaw VPS login, proxy behavior, streaming, and server-side cancellation have not been validated in this release work. Existing OpenClaw native features and the Hermes webpage adapter have automated regression coverage; they do not imply complete remote Gateway management.

### Earlier changes

- `5.3.10` refined Settings with Mica, consistent cards, responsive controls, save progress, and a compact About page.
- `5.3.9` introduced verified portable updates, save-and-restart installation, startup-failure rollback, and startup/Settings reliability fixes. It is the first release containing the updater.

The `5.3.8` release added the following:

- Add opt-in OpenClaw Gateway v4 controls for session browsing and search, empty-session creation, model selection, exact run cancellation, and observed run completion notifications. Chat input, attachments, and rendering remain in the hosted Web UI.
- Configure the native connection per environment in Settings, with protected credentials, a persistent signed device identity, optional Cloudflare Access user tokens, and explicit pairing and connection feedback. Native Gateway authentication does not automatically share the web page's login or cookies.
- Add a session workspace, responsive toolbar actions, first-run guidance, and Settings draft/save feedback.
- Update the purple/pink branding across window, tray, application, and DPI assets. A targeted Windows shell icon refresh tool is available when Explorer retains the old executable icon.
- Reduce the DOM inspection work that contributes to chat-page stalls through bounded text sampling, early model-control matches, and coalesced mutation checks. A connected run with quiet visible output no longer triggers automatic resync or reload solely because it is quiet; disconnected and stalled-connection recovery remains available.
- Performance validation used a synthetic WebView2 page with **1,000 messages**. This measures bridge inspection cost, not typing, scrolling, or folding frame rates in a real authenticated deployment. Real VPS / Cloudflare end-to-end testing is still required.
- WebView2 remains the rendering engine and its Runtime remains an external dependency. This release does not bundle a standalone Chromium engine. See [OpenClaw integration](docs/openclaw-integration.md) for authentication boundaries and validation details.

- `5.3.7` improves status bar reliability: all status controls (Model, Heartbeat, Latency, Access, Work Status) now display "N/A" when information is unavailable instead of showing blank or default placeholder text. This provides clear visual feedback when detection fails.
- OpenClaw model detection is enhanced with multiple fallback patterns: direct property reading from `<openclaw-app>` element (if exposed), class pattern matching (`[class*="model-select"]`), ARIA label extraction from model-related buttons, and button text pattern detection. This multi-layered approach makes model detection more resilient to UI changes while maintaining the source code reading approach.
- `5.3.6` protects settings after read/write failures and merges unrelated changes from multiple instances. Conflicting edits are rejected without overwriting the other instance's settings.
- Native commands now await owned asynchronous replies, and activity detection follows the newest chat output. Logs and diagnostic exports redact URLs, credentials, and activity text.
- The connection error page shows the actual failure, keeps Retry and Settings visible, follows the app language/theme, and retries the gateway. Environment metadata changes preserve the active session, and the shell toolbar adapts to narrower windows.
- `5.3.4` adds native run completion notifications. When a hosted run finishes (busy→idle transition), Harness raises a Windows balloon notification through the system tray. This is the signal a browser tab cannot deliver on its own. Settings: `Notify when a run finishes` (default on), `Only when Harness is not focused` (default on, suppresses redundant notifications while you are watching), and `Forward notifications from the page` (default on, passes web notifications raised by the hosted UI through to Windows). The notification system respects Focus Assist (quiet hours).
- `5.3.4` adds multi-backend support with `HostedBackendKind` (OpenClaw, DeepSeekWeb, Generic) and `HostedBackendProfile`. Each backend defines its own latency probe path, app state selectors, and vocabularies (busy/abort text matching). OpenClaw uses `__openclaw__/a2ui/` probe and English stop words; DeepSeek uses gateway root probe and CJK + English vocabularies. DOM probing and command dispatch now support both OpenClaw-specific methods and generic CustomEvent fallback. Backend selection is available in Settings under Environments, with automatic topology detection requiring WebView recreation on backend changes.
- `5.3.3` adds an `Auto refresh` switch under General. When disabled, automatic session recovery, navigation retries, heartbeat-triggered reloads, and timeout-triggered WebView recreation stop; manual Reload/Retry, startup, and environment changes remain available.
- `5.3.2` merges the separate Minimize to tray and Close to tray options into one System tray setting. Minimizing always stays on the taskbar; when System tray is enabled, closing hides Harness to the system tray, and when disabled, closing exits the app.
- `5.3.1` shows `N/A` instead of `ERR` in the top status bar's latency badge when the Control UI latency probe fails.
- `5.3.0` renames the application from `OpenClaw Manager` to `Harness` across the solution, both projects, the test project, every namespace, the executable, and all user-visible surfaces. See [Why the rename](#why-the-rename) for what did and did not move.
- **Breaking:** local data moved from `%LOCALAPPDATA%\OpenClaw\` to `%LOCALAPPDATA%\Harness\` with no automatic migration, so 5.3.0 starts with a clean profile. Re-add gateway environments in Settings and sign in to the hosted Control UI again. The old folder is left in place untouched.
- Harness uses the purple and pink layered logo in `Assets/harness-logo.png`. Window/tray icons, tiles, splash, store, lock-screen, and in-app images are generated with `python tools/regenerate-assets.py` (Pillow required), including 100–400% DPI assets and a multi-resolution `WindowIcon.ico` (16–256px).
- The published output now carries the app's generated `.pri` and its `Assets/` folder. Before this, `dotnet publish` produced a folder that threw `XamlParseException` on the first window and ran without any icon.
- `5.2.0` hardens persistence and recovery after an engineering review: a corrupt `settings.json` is backed up to `settings.json.corrupt-<timestamp>` before defaults are written, and a transient read failure (file lock, permissions) no longer overwrites the user's settings file.
- Per-environment WebView2 session isolation now uses an explicit `CoreWebView2Environment` created with the environment's user data folder. The previous `WEBVIEW2_USER_DATA_FOLDER` environment-variable approach only affected the process-wide default environment and could silently share cookies/session state across gateway environments after a runtime switch.
- Cloudflare Tunnel outages are classified from HTTP `530` behind Cloudflare (`cf-ray` present); the unreachable `1033` status-code branch was removed.
- Log retention now re-runs when the UTC date rolls over, so a tray-resident instance no longer accumulates expired logs; a global hotkey that fails to parse or register shows a localized InfoBar warning instead of only logging.
- Dead configuration options (`eventIdleSuspicionSeconds`, `transportIdleSuspicionSeconds`, `enableTelemetryCollection`, `telemetryIntervalSeconds`) were removed from `settings.json`; they were persisted but never read.
- The Core regression test project (`tests/Harness.Core.Tests`, 32 tests) and the Windows CI workflow are restored; the repository guardrail now requires the test project instead of forbidding it.
- `5.0.1` updates the Gateway/Cloudflare status model for VPS deployments behind Cloudflare Tunnel: heartbeat, diagnostics, and latency probes now share one HTTP status classifier, the latency probe targets the documented `__openclaw__/a2ui/` Control UI path, and proxy/path failures such as 404, 405, and 5xx no longer publish as healthy latency.
- Settings persistence failures now flow back to the Settings dialog instead of being logged while the dialog closes as if the save succeeded.
- `5.0.0` remains the previous refactor-validation baseline. The v3.3.6 cleanup remains the reviewed baseline; this is not intended to rewrite the historical `v3.0.5` / `v3.0.1` / `v3.0.0` release entries in [changelog.md](changelog.md).
- [docs/code-style.md](docs/code-style.md) is the canonical code-style and architecture guide for this branch.
- The solution maps `Harness.Core` from the active `x64`/`x86`/`ARM64` solution platform to the Core project's platform-independent `AnyCPU` configuration, so VS2026 can load Debug/Release x64 without configuration-manager repair.
- The default `https://example.com` environment is treated as a first-run placeholder, not as a real Control UI. While it is selected, MainWindow skips WebView2 host creation, stops heartbeat/latency probes, clears any previous WebView host, and shows the localized "Configure a Gateway URL in Settings" status instead of navigating to `example.com` or leaving the loading ring active.
- Saved language preference now uses the Windows App SDK `Microsoft.Windows.Globalization.ApplicationLanguages` API, so startup applies the configured language without the previous WinRT `Language override failed` warning.
- Runtime ownership is split across focused services: `WebViewStatusInspector`, `HeartbeatRuntime`, `GatewayHeartbeatTransport`, `HostedSessionHeartbeatPolicy`, `WebViewRecreationService`, `SettingsPersistenceAdapter`, `LiveShellSettingsApplier`, and `StatusPresenter`.
- `WebViewService.cs` now keeps shared fields, construction, events, and public navigation commands; WebView2 initialization/detach/dispose/current-target checks live in `WebViewService.Lifecycle.cs`, and profile/session operations live in `WebViewService.Session.cs`.
- `WebViewService` navigation code is split by responsibility: event/completion flow in `WebViewService.Navigation.cs`, host-message handling in `WebViewService.HostMessages.cs`, shared navigation ownership/cancellation helpers in `WebViewService.NavigationState.cs`, watchdog ownership in `WebViewService.NavigationWatchdogs.cs`, CoreWebView2 command wrappers in `WebViewService.NavigationCommands.cs`, page-token/session-ready retry in `WebViewService.PageToken.cs`, and process-failure/auto-retry recovery in `WebViewService.NavigationRecovery.cs`.
- Hosted bridge logic is split into embedded JavaScript assets for host messaging, mutation filtering, MODEL resolution, DOM fallback, activity/stale-busy tracking, phase classification, command dispatch, and status inspection.
- `WebViewStatusInspector` keeps shared state, public entry points, and snapshot publication in the main partial, with direct inspection/coalescing, post-navigation probes, Control UI snapshot parsing, and bounded script execution split into focused partials.
- WebView status probes are generation-scoped, accepted-page-version scoped, timeout-bounded, UI-dispatched before touching WebView2, and protected by owner/page-token validation so stale or cancelled inspections cannot overwrite current state; timeout, script failure, or exhausted post-navigation probes publish an owned terminal `Unavailable` snapshot, downgrade stale `Connected` shell state, move coordinator recovery state out of stale Ready/Healthy projections, show a visible InfoBar while reconnecting, stop post-navigation page-script probing, and preserve the last non-empty MODEL value through loading, unavailable, and unknown snapshots for the same accepted page.
- Hosted bridge commands and WebView stop/abort scripts have bounded execution timeouts and reject results after the WebView target or accepted page ownership changes; recovery-owned bridge commands link the active recovery cancellation token through UI dispatch and script execution, CustomEvent command fallback is no longer reported as handled unless a hosted bridge method actually accepts the command, and Stop fallback stays bound to the original page target so a stale command rejection cannot stop a newer page.
- WebView navigation, reload, manual retry, and auto-retry invalidate page ownership before issuing CoreWebView2 commands; manual Retry keeps a localized actionable error visible when no retryable navigation exists, recovery-owned reloads carry the active recovery cancellation token and check whether reload actually started before advancing, stale auto-retry continuations exit without publishing old navigation state, page-token retry plus native-triggered `session-ready` replay use a lease-owned navigation cancellation scope so reload/detach/new navigation cancels old work without disposing tokens still in use, exhausted or failed retries surface as Error instead of leaving the shell in Reconnecting, exhausted page-token capture publishes `Unavailable` only against the original navigation generation, and Stop fallback cancels navigation watchdogs/probes/page ownership so a user stop cannot later trigger stale startup timeout recovery.
- WebView startup recovery tolerates a missing `NavigationStarting` callback by letting only a target-matching `NavigationCompleted` claim the pending navigation, records the previous source for stale-completion rejection, preserves the pending target for a bounded post-timeout recovery window, recreates navigation cancellation ownership for a still-current late successful completion after completion-timeout recovery, cancels queued, deferred, or active timeout-driven WebView recreation when a late completion recovers the page, projects unexpected completion-handler failures as `Unavailable` plus Error instead of leaving Loading stale, preserves higher-priority settings/initial/session/topology recreation reasons when timeout recovery requests merge into the queue, guards completion timeouts with an active watchdog id, and keeps the full-window loading ring tied only to browser navigation `Loading` rather than the longer `GatewayConnecting` status phase.
- Dynamic WebView recreation now waits for the window shell, host panel, and new WebView2 child control to be visible and non-zero sized before initialization/navigation; compact, hidden-to-tray, or minimized startup states defer recreation without losing the original higher-priority reason, child layout timeouts are requeued through the normal recreation timer/circuit-breaker path and counted by the circuit breaker instead of retrying forever or waiting for another window event, unexpected recreation exceptions surface a localized actionable error with Retry instead of only logging after timeout recovery hid the InfoBar, and deferred recreation ownership now lives in `WebViewRecreationService` rather than the window partial.
- WebView detach/recreation and resource-stop paths now reset visible heartbeat, latency, MODEL, access, work, and shell projections before the replacement session reports state, so a stopped probe or failed recreation cannot leave stale `HB OK`, ping, `AUTH OK`, `LIVE`/`IDLE`, or previous MODEL values visible as if the old session were still current.
- Hosted bridge session/status messages use host-generation and owner/page-token ownership checks instead of CoreWebView2 wrapper reference identity, so valid `session-ready` and status posts are not dropped because WebView2 exposes a different COM wrapper object.
- WebView process failures retire navigation retry/replay cancellation before publishing the unavailable state, and injected ViewModel UI updates catch/log callback failures so dispatcher callbacks do not escape as unhandled UI-thread exceptions.
- Gateway heartbeat, diagnostics, and latency probes now share Gateway HTTP status classification for Cloudflare Tunnel and reverse-proxy deployments. Heartbeat treats proxy/path/server failures as failures, device approval or rate-limit responses as session-blocked user-action states, and hosted-session inspection `Unavailable` as a failure instead of falling through to healthy HTTP transport; latency probes use the documented `__openclaw__/a2ui/` Control UI path and do not record 404/405/5xx/1033 responses as healthy samples.
- ShellSessionCoordinator recovery work is cancellable across event gaps, stale-busy recovery, heartbeat-triggered recovery, foreground resume, in-page bridge commands, and UI-dispatched reloads; public recovery requests link caller cancellation into the active operation before queueing inspections, bridge commands, or reloads, and attach/detach/reset/dispose cancels pending work before replacing WebView or bridge services so old recovery decisions cannot run against a new hosted session.
- Settings changes for global hotkey, always-on-top, and multiple-instance behavior apply to the running process; Settings save merges only fields edited in the open dialog so stale snapshots do not overwrite live Pin/hotkey/environment changes, same-value two-way binding writes do not mark settings fields dirty, multiple-instance listener changes are observed asynchronously so Settings save does not wait on named-pipe shutdown, secondary-launch activation/takeover waits are async and share one bounded takeover deadline so relaunch handoff does not block startup, and the cross-process single-instance lock uses a named semaphore while shutdown waits for the named-pipe listener to stop before releasing ownership; compact mode uses visual states that collapse nonessential fixed-width top-bar segments at 480px, keeps the loading ring collapsed while compact, and validates saved compact positions against current display work areas; Log Viewer plus latency-probe refresh/stop races are guarded with run-id and selected-host checks; long-running async commands reject repeat activation while running, diagnostic bundle export moves log enumeration/zip work off the UI thread, and inactive WebView2 profile deletion runs on a background thread.
- The local C# regression harness is intentionally absent in this checkpoint; active local verification is restore/build/format, repository guardrails, bridge script checks, whitespace checks, and VS2026 manual debug.
- Detailed implementation history is kept in [changelog.md](changelog.md) and [docs/superpowers/plans/2026-05-23-deep-refactor-hardening.md](docs/superpowers/plans/2026-05-23-deep-refactor-hardening.md).

### This project is

- A WinUI 3 + WebView2 remote management shell
- A Windows-native workspace with dedicated Hermes Agent and OpenClaw integration and ordinary web app hosting
- A thin client that enhances the existing web UI with native UX

### This project is not

- A local Gateway or node host
- A full native rewrite of the OpenClaw frontend
- An offline-capable standalone application

---

## Features

| Feature | Description |
|---|---|
| WebView2 Shell | Hosts the remote Control UI inside a native window |
| OpenClaw Gateway Controls | Optional session workspace, model selection, exact run cancellation, and run notifications |
| Environment Switching | Manage multiple hosted Control UI endpoints |
| Connection Status | Status bar, error InfoBar, retry support |
| Auto-Reconnect | Retries failed navigation automatically when Auto refresh is enabled |
| Heartbeat | Periodic Control UI and transport probe with configurable reconnect thresholds |
| System Tray | Configurable close-to-tray behavior; minimizing stays on the taskbar, with Open Harness, Reload, View Logs, Compact Mode, Settings, and Exit actions |
| Global Hotkey | Configurable system-wide hotkey (default Ctrl+Alt+Space) to show/hide the window, with Settings UI validation and reset |
| Instance Control | Optional multiple-instance mode; off by default so app relaunches restore the existing tray-hidden window |
| Session Isolation | Separate WebView2 profile data per configured environment |
| Latency Tooltip | Hover the latency badge for recent min/avg/p95/max round-trip stats and Cloudflare PoP |
| Always on Top | Pin button to keep the window above other applications, with native topmost fallback and distinct active/inactive colors |
| Compact Mode | Reduced control/status window for screen-corner placement |
| Diagnostic Export | One-click export of redacted settings, logs, and runtime info as a zip bundle |
| Theme | Top-bar segmented switcher for System, Light, and Dark |
| Language | English, Simplified Chinese, System |
| Diagnostics | Runtime, network, and session checks |
| Log Viewer | View today's log and open the log folder |
| DevTools | Open WebView2 developer tools |

---

## Tech Stack

| Component | Version |
|---|---|
| .NET | 10.0 |
| Windows App SDK | 1.8.x |
| WebView2 | SDK supplied via WinAppSDK; Runtime installed separately |
| MVVM | CommunityToolkit.Mvvm 8.x |
| UI Language | English + Simplified Chinese |
| Packaging | Unpackaged, self-contained |

---

## Architecture

```text
Harness
|- MainWindow (WinUI shell: XAML, WebView2 control swap, tray/window integration)
|  |- WebViewRecreationService
|  |- LiveShellSettingsApplier
|  |- SettingsDialog / SettingsPersistenceAdapter
|  `- MainViewModel (orchestration and bindable state)
|     |- StatusPresenter
|     |- UiTaskDispatcher
|     |- WebViewMessageOwnership
|     |- WebViewService
|     |  |- WebViewStatusInspector / WebViewStatusInspectionScripts
|     |  |- HeartbeatRuntime
|     |  |- GatewayHeartbeatTransport / HostedSessionHeartbeatPolicy
|     |  |- WebViewGenerationTracker
|     |  `- WebView command JS assets
|     |- HostedUiBridge
|     |  `- embedded bridge JS assets
|     |- ShellSessionCoordinator adapters
|     `- ControlUiLatencyService
`- Harness.Core
   |- settings/configuration models
   |- recovery policy/state machine
   |- diagnostics/log utilities
   `- parser/policy helpers
```

Design principle: remote-first thin shell. The actual OpenClaw runtime lives on the VPS; this app is a native control surface for the hosted Control UI.

---

## Project Structure

```text
Harness/
|-- NuGet.config
|-- Harness.sln
|-- DEVELOPMENT_NOTES.md
|-- README.md
|-- readme_zh.md
|-- src/
|   |-- Harness/
|   |   |-- Harness.csproj
|   |   |-- Package.appxmanifest
|   |   |-- app.manifest
|   |   |-- App.xaml
|   |   |-- App.xaml.cs
|   |   |-- MainWindow.xaml
|   |   |-- MainWindow.xaml.cs
|   |   |-- Assets/
|   |   |-- Abstractions/
|   |   |-- Helpers/
|   |   |-- Services/
|   |   |-- Strings/
|   |   |-- Styles/
|   |   |-- ViewModels/
|   |   `-- Views/
|   `-- Harness.Core/
|       |-- Harness.Core.csproj
|       |-- Helpers/
|       |-- Models/
|       `-- Services/
```

### Key folders

- `Services/`: configuration, logging, diagnostics, WebView2 lifecycle, recovery helpers
- `Harness.Core/`: physical source tree for pure .NET shared code used by the WinUI app
- `Styles/`: shared WinUI typography, spacing, and status resources
- `ViewModels/`: shell state, commands, settings editing
- `Views/`: settings, about, and log viewer dialogs
- `Strings/`: localized UI resources

---

## Development Prerequisites

- Windows 11 24H2 (build 26100) or later; older Windows versions are not validated for this release
- Visual Studio 2026
- .NET Desktop Development workload
- Windows App SDK C# templates
- .NET 10 SDK

### Dependency Restore Notes

- The solution uses SDK-style projects with `PackageReference`.
- Repository-local `packages/` folders are not required and can be deleted safely.
- A solution-level [Directory.Build.props](Directory.Build.props) enables NuGet lock files and static graph restore for repeatable SDK-style restores.
- The expected workflow after clearing local caches is:

```powershell
dotnet restore Harness.sln --locked-mode
dotnet build Harness.sln -c Debug -p:Platform=x64 --no-restore
```

The app project selects the Windows runtime for the chosen platform and verifies native dependencies after building. Run `bin\Debug\5.3.12\net10.0-windows10.0.26100.0\win-x64\Harness.exe` for local x64 testing. The older output without the `win-x64` directory is incomplete and must not be used. Debug builds require .NET 10 and WebView2 Runtime on the machine.

If the workspace has a Windows Low integrity label, its build outputs can inherit that label. Even launching the EXE from Explorer can then run Harness at Low integrity, preventing writes to its normal browser data and logs and potentially preventing tray registration. The current Windows build and publish targets set Medium integrity on the Harness and updater apphosts; test builds also normalize the test hosts. Update preparation applies the same label to the three staged apphosts before handing off to the updater. These operations change only executable integrity labels, preserving directory permissions, DACLs, and browser data. Use `icacls <path>` to inspect labels when diagnosing this combination of failures. For manual deployment, copy or extract the complete build output into a normal user directory.

### Code Style

- `.editorconfig` is the root style contract: LF endings, final newline, trimmed trailing whitespace, four-space C#/XAML indentation, required braces, and SDK-enforced formatting diagnostics.
- See [docs/code-style.md](docs/code-style.md) for project-specific architecture boundaries, partial-class ownership, XAML resource rules, Core physical-source rules, and verification commands.
- Run `$env:Platform='x64'; dotnet format Harness.sln --verify-no-changes --no-restore` before committing style-sensitive changes.

### Active Verification

Automated verification is restore, the Core regression test suite, x64 build, format, repository guardrails, bridge script checks, OpenClaw page selection checks, and whitespace checks. The same steps run in CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)):

```powershell
dotnet restore Harness.sln --locked-mode
dotnet test tests\Harness.Core.Tests\Harness.Core.Tests.csproj -c Debug --no-restore
dotnet build Harness.sln -c Debug -p:Platform=x64 --no-restore
$env:Platform='x64'; dotnet format Harness.sln --verify-no-changes --no-restore
powershell -ExecutionPolicy Bypass -File tools\verify-repo-structure.ps1
powershell -ExecutionPolicy Bypass -File tools\verify-bridge-scripts.ps1
powershell -ExecutionPolicy Bypass -File tools\verify-openclaw-selection.ps1
git diff --check
```

`tools\verify-bridge-scripts.ps1` requires Node.js by default because it is the active behavior check for embedded bridge assets. Set `OPENCLAW_NODE` to a specific Node executable when the default `node` on `PATH` is blocked or unavailable; set `OPENCLAW_ALLOW_NODE_SKIP=1` only for an explicit local skip.

`tools\verify-openclaw-selection.ps1` checks the authored page selection script, including session identity and draft/attachment guards. It requires Node.js and accepts `OPENCLAW_NODE` as an executable override.

VS2026 manual debug remains required for real WebView2, Gateway, Cloudflare Tunnel, tray, hotkey, and compact-mode behavior.

The DeepSeek Harness Web UI previously passed a manual web test; it is now classified as an ordinary web app. Hermes web adaptation is included in 5.3.12 and still requires validation with your deployed official web interface; native Hermes Gateway management is not implemented.

Manual debug should explicitly cover:

- real hosted Gateway load, task submission, streaming output, and completion without manual reload
- MODEL display after startup, session switch, page reload, and native-triggered `session-ready` replay
- Cloudflare Tunnel or reverse-proxy 5xx pages, unexpected 4xx pages, auth/approval pages, origin rejection, and recovery after the upstream becomes healthy again
- latency tooltip `cf-ray` / Cloudflare PoP parsing on a real tunnel response
- tray show/hide, close-to-tray, reload, compact-mode menu entry, and single-instance relaunch handoff
- global hotkey and always-on-top changes saved from Settings without restarting
- compact-mode entry/exit at 480px and full-mode window-bounds restore after relaunch
- title-bar/DWM border color in light/dark/theme-switch paths, including the top 1px edge
- Log Viewer repeated refresh and close-while-loading behavior

### Current Limitations

- The in-repo C# regression suite covers Core logic (configuration, classification, recovery state machine, helpers); WinUI shell behavior is still verified by guardrail scripts, bridge script checks, and VS2026 manual debug.
- Bridge script behavior is covered by `tools\verify-bridge-scripts.ps1`, but browser-runtime behavior still needs WebView2/VS2026 debug.
- `WebViewService` is split into focused partials; new lifecycle, navigation, inspection, heartbeat, command, and profile/session behavior should stay in the matching partial instead of the root file.
- Real Gateway, Cloudflare Tunnel, reverse-proxy error pages, tray, hotkey, single-instance, DWM title-bar, and compact-mode behavior still need VS2026 manual debug.
- Native OpenClaw Gateway v4 controls use a separate connection and device identity. Web login does not authenticate this connection; real VPS / Cloudflare end-to-end validation remains required.
- The 1,000-message synthetic WebView2 performance experiment measures bridge inspection work and does not establish real-page interaction frame rates.

### Development Notes

See [DEVELOPMENT_NOTES.md](DEVELOPMENT_NOTES.md) for lessons learned from native window chrome, theme synchronization, and other maintenance-sensitive areas.

---

## Getting Started

### Visual Studio

1. Open [Harness.sln](Harness.sln) in Visual Studio 2026.
2. Set solution platform to `x64`.
3. Press `F5` to run.

### CLI

```powershell
dotnet restore Harness.sln --locked-mode
dotnet build Harness.sln -c Debug -p:Platform=x64 --no-restore
```

### Release Packaging

From the repository root, use PowerShell 7 to build the self-contained Windows x64 package:

```powershell
dotnet restore Harness.sln --locked-mode
pwsh -NoProfile -File tools/publish-release.ps1 -OutputRoot artifacts/release
```

The script creates a new version directory such as `artifacts/release/5.3.12`, validates the published files, and produces a ZIP and SHA-256 checksum. It refuses to overwrite an existing version directory; choose another `OutputRoot` for a repeat build.

Portable builds from this source include **Settings > About > Check for updates**, verified downloads, and **Save settings and restart to update**. The updater keeps the installation path and AppData, preserves extra user files, and rolls back if the new app fails startup. Existing published 5.3.8 packages require one manual upgrade to 5.3.9, which includes the updater. See [portable updates and recovery](docs/portable-updates.md).

Pushing a numeric version tag such as `5.3.12` (without a `v` prefix) triggers [.github/workflows/release.yml](.github/workflows/release.yml). The tag must match the project version and have release notes in `docs/releases/<version>.md`. The workflow runs validation, packages the release, and publishes the ZIP and SHA-256 checksum to GitHub Releases.

### First Launch

1. The app starts with a placeholder environment and does not navigate WebView2 until a real Control UI URL is configured.
2. Open Settings from the top bar.
3. Add your public hosted Control UI URL, for example `https://your-gateway.example.com`.
4. Save settings and the embedded WebView2 shell will load the remote UI.

### Cloudflare Tunnel / VPS Notes

If your OpenClaw Gateway runs on a VPS behind Cloudflare Tunnel:

- use the public HTTPS Control UI URL for the hosted workspace
- use the separate optional Gateway WebSocket field for native controls; do not put a WebSocket URL in the workspace URL field
- make sure the same public origin is listed in `gateway.controlUi.allowedOrigins`
- if you use `gateway.auth.mode: "trusted-proxy"`, make sure identity headers are forwarded on both HTTP requests and WebSocket upgrades
- do not mix trusted-proxy mode with shared token auth unless upstream explicitly documents that combination as supported
- avoid same-host loopback reverse proxies for trusted-proxy mode; use token/password auth there instead
- make sure the tunnel or reverse proxy preserves the original host and scheme

If the page loads but OpenClaw reports origin rejection, check the exact public origin string and proxy forwarding rules first.

---

## Settings

The Settings window is organized into five sections:

| Section | Content |
|---|---|
| Language | Display language |
| General | Window, tray, global hotkey, always-on-top, and multiple-instance behavior |
| Environments | Hosted Control UI endpoints and optional OpenClaw Gateway connection, credentials, and Cloudflare Access token |
| Sessions | Clear WebView2 session data for a specific environment |
| Developer Tools | Diagnostics, logs, DevTools |

### Environment URL Rules

- Use the hosted Control UI page URL with `http://` or `https://`
- Do not put a `ws://` or `wss://` Gateway address in the hosted workspace URL field; native controls have a separate optional WebSocket field
- For Cloudflare Tunnel or reverse-proxy deployments, always use the exact public browser-facing origin

---

## Data Storage

All local data is stored under `%LOCALAPPDATA%\Harness\`.

| Path | Content |
|---|---|
| `settings.json` | Environment configs, theme, language, tray and instance behavior, heartbeat settings, window state |
| `logs/` | Daily log files |
| `WebView2Data/` | WebView2 profile data, cookies, cache |

---

## Runtime Requirements

The current Release targets **Windows 11 24H2 (build 26100) or later**. Older Windows versions have not been validated. The self-contained distribution includes .NET and the Windows App SDK; WebView2 Runtime is an external prerequisite:

| Dependency | Download |
|---|---|
| WebView2 Runtime | [Download](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

Windows 11 usually already includes WebView2 Runtime. Install it if it is missing; the Release package does not include a fixed WebView2 Runtime or standalone Chromium engine. Framework-dependent builds additionally require the matching [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

---

## Changelog

See [changelog.md](changelog.md) for the full release history.
