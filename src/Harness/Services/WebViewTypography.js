// Copyright (c) Lanstack @openclaw. All rights reserved.
(() => {
  const options = __HARNESS_TYPOGRAPHY_OPTIONS__;
  const root = document.documentElement;
  if (!root || (window.__harnessTypographyRevision || 0) > options.revision) return;
  window.__harnessTypographyRevision = options.revision;

  const systemFamily = 'system-ui, "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", sans-serif';
  const family = options.family === 'Noto Sans SC' ? '"Harness Noto Sans SC", ' + systemFamily : systemFamily;
  const size = Number.isInteger(options.size) && options.size >= 10 && options.size <= 32 ? options.size : 14;
  const fontUrl = /^https?:$/.test(window.location.protocol)
    ? window.location.origin + '/__harness__/fonts/NotoSansSC.ttf'
    : 'https://harness-fonts.invalid/__harness__/fonts/NotoSansSC.ttf';
  let style = document.getElementById('harness-typography');
  if (!style) {
    style = document.createElement('style');
    style.id = 'harness-typography';
    (document.head || root).appendChild(style);
  }
  style.textContent = `
    @font-face {
      font-family: "Harness Noto Sans SC";
      src: url(${JSON.stringify(fontUrl)}) format("truetype");
      font-style: normal;
      font-weight: 100 900;
      font-display: swap;
    }
    :root {
      --font-body: ${family} !important;
      --font-sans: ${family} !important;
      --font-family-sans: ${family} !important;
      font-family: ${family} !important;
      font-size: ${size}px !important;
    }
    body {
      font-family: ${family} !important;
      font-size: ${size}px !important;
    }
    :where(div, span, p, a, h1, h2, h3, h4, h5, h6, label, button, input, textarea, select, option, summary, th, td, li):not(:where(pre *, code *, kbd *, samp *, svg *, .monaco-editor *, .xterm *, [class*="mono"], [class*="mono"] *, [class*="icon"], [class*="icon"] *, [class*="Icon"], [class*="Icon"] *, [class*="material-symbols"], .fa, .fas, .far, .fab, [data-icon], [data-icon] *, [aria-hidden="true"], [aria-hidden="true"] *)) {
      font-family: ${family} !important;
    }
    :where(.chat-text, .chat-message-content, .message-content, .markdown, .markdown-body, .prose) {
      font-size: ${size}px !important;
    }
    :where(input, textarea, select, button):not(:where(pre *, code *, [class*="icon"], [class*="Icon"], [data-icon])) {
      font-size: inherit !important;
    }
  `;
})();
