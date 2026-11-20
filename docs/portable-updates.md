# Portable updates

Harness continues to ship as a Windows x64 ZIP. The published app needs no installer,
administrator service, or scheduled update task. Microsoft Edge WebView2 remains a
system prerequisite. Update checks happen only when requested in Settings > About.

## User flow

1. Choose **Check for updates**. Harness reads the latest stable GitHub Release from
   `guijianchou/Harness`, compares numeric versions, and checks that the matching x64
   ZIP and checksum are present. Drafts, previews, older versions, and incompatible
   packages are not installed. GitHub errors are reported as errors, not “up to date”.
2. Choose **Download update**. Download progress and cancellation remain available.
   Harness checks the ZIP size and SHA-256, validates every package file, and stages
   the new version beside the existing program folder. This requires free space for
   the download, extracted package, updater, backup, and any local extra files.
3. Finish chat work, close other Harness instances, and choose **Save settings and
   restart to update**. The settings draft is saved first; validation or save failure
   keeps the app open. A separate updater verifies the staged package and signals
   that it is ready before Harness exits normally.
4. The updater waits for the initiating process to exit and takes exclusive ownership
   of that installation. It preserves user-added files, keeps the old directory as a
   backup, replaces the program directory, and starts the new executable. The new
   version must acknowledge creation and activation of its main window and remain
   running through a short grace period. Gateway connectivity is not an update health
   requirement.

The installation path stays the same, so shortcuts and startup registration remain
valid. Configuration, browser profiles, and credentials stay in `%LOCALAPPDATA%\Harness`.
No chat is submitted, no draft is sent, and no remote credentials are included in
GitHub requests. HTTPS and the checksum detect transfer corruption; the checksum is
published by the same GitHub repository and is not an independent digital signature.

Official builds that include `portable-manifest.json` and `Updater/Harness.Updater.exe`
support replacement. Debug outputs and older portable releases still allow version
checks and provide the release-page link. **Existing 5.3.8 releases need one manual
upgrade to 5.3.9, the first published portable package containing the updater.** Do not
overwrite or retag the existing 5.3.8 release to deliver this feature.

## Failure and recovery

- Download, cancellation, checksum, package, and preparation failures leave the
  current installation untouched. Closing Settings cancels preparation and removes
  staging owned by that settings window when no helper has been handed control.
- Other running copies of the same portable installation prevent replacement. The
  updater does not kill them. Close them and try again.
- Package-owned obsolete files disappear with the old version. User-added files are
  copied to the new directory and also remain in the backup. A user file that collides
  with a new package file causes an update failure instead of silent overwrite.
- If replacement or candidate startup fails, the updater restores and restarts the
  previous directory. It stops only the candidate process it launched when that
  process fails the startup check. If another process prevents rollback, it retains
  both directories and reports that manual recovery is required.
- The sibling `.harness-update-<id>` directory contains the helper, transaction result,
  and `backup` (or `failed-version` after rollback). After confirming the new app works,
  it can be removed manually. No automatic pruning of backups is performed.
- A power loss or updater termination can interrupt the two directory moves. Close
  all Harness instances; keep any incomplete installation directory aside; move the
  retained `backup` directory back to the original program path; then start its
  `Harness.exe`. Do not delete `%LOCALAPPDATA%\Harness`.

## Release contract and validation

`tools/publish-release.ps1` publishes both the WinUI app and a self-contained updater,
then generates `portable-manifest.json` with schema 1, version, runtime, and every
package file's size and SHA-256. ZIPs contain files at their root. CI and the release
workflow run Core update tests and `tools/verify-portable-update.ps1`, which uses the
real updater executable and isolated candidate processes to verify process exit,
startup acknowledgement, rollback/restart, and user-file preservation. Its fixtures
are not a substitute for WinUI visual validation or real authenticated Gateway tests.

The updater rejects path traversal, alternate streams, device names, case-insensitive
duplicate paths, symlinks/reparse points, unexpected files, and excessive archive sizes.
Updates are limited to local writable folders and x64. No elevation is attempted.

中文使用说明：进入“设置 > 关于”，检查并下载更新，完成当前聊天工作、关闭其他实例后点击
“保存设置并重启更新”。设置保存失败时不会退出；下载与校验失败时不会替换程序；新版启动失败
会恢复旧版。旧程序保存在相邻的 `.harness-update-<id>\backup` 中，确认新版正常后可自行清理。
如果断电导致更新中断，请关闭所有实例，保留失败目录，再将备份移回原程序路径。
之前发布的 5.3.8 需要先手动升级一次到包含更新器的 5.3.9 便携包。
