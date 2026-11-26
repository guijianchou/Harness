# Font preferences

Settings > Appearance places **Font** and **Font size (px)** directly below Language.
The default is **System UI, 14 px**. **Noto Sans SC** is bundled with its SIL Open
Font License and works without installing a font or downloading one at runtime.
Both choices support English and Simplified Chinese.

Save and close applies these preferences to the native interface and the hosted
page. Cancel discards the draft. Existing settings files use the defaults until
edited. The accepted base size is an integer from 10 to 32 px. Native headings and
secondary labels retain their relative sizes; icons and monospaced code/log fonts
retain their own families. Web content zoom remains a separate preference.

The hosted page uses an idempotent stylesheet, reapplied on document load and
preference changes. Noto is served from a reserved same-origin font request that
WebView2 answers locally. No remote font service is contacted, and page content
security policies are not weakened. A page with a restrictive style policy,
closed shadow roots, or custom typography can still require page-specific support.

## Verification

- Settings reliability regression covers defaults, durable saves, unchanged stale
  drafts, invalid sizes and committed-settings application.
- Bridge verification covers idempotent updates and stale-script suppression.
- Native WebView regression checks actual bundled-font loading, Chinese/English
  text, code font preservation and computed sizes under a same-origin font policy.
- Manual WinUI check: choose each font at 14 and 18 px, save, reopen Settings and
  restart. Check the toolbar, settings controls, flyouts and log viewer; repeat at
  10 and 32 px in a narrow window. Confirm cancel preserves the saved appearance
  and that Chinese labels, icons and keyboard focus remain readable.

## 简体中文

“设置 > 外观”在“语言”下方提供“字体”和“字号（px）”，默认使用
System UI 和 14 px。Noto Sans SC 随程序打包，两项均支持中英文。
保存后同时应用到原生界面和内嵌页面，取消则丢弃修改。
字号支持 10–32 px 整数，标题保留相对层级，代码和日志保留等宽字体。
网页内容缩放独立设置。字体无需联网下载，也无需安装到系统。
