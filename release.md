## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed. The bundled runtime is always the latest .NET 8 release available at the time this version was built.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

> **Note — update size:** Some updates download more than usual because they include a refreshed bundled .NET runtime, which is delivered in full for reliability. These runtime refreshes aren't listed separately in the notes, so an occasionally larger update is expected.

> **Note — Remote Desktop (RDP):** If Text Template Manager runs on both your local computer and a remote computer you connect to over RDP, the global Quick Paste hotkey always opens Quick Paste on the **local** computer — Windows delivers a registered global hotkey locally, so it never reaches the remote session. Give each machine a **different** Quick Paste hotkey, or run only one instance, to avoid the clash.

### What's new in this release

The rich-text editor's code blocks got a big upgrade:

- **Syntax highlighting with a language picker.** Code blocks now colour your code. A picker in the block's top-right corner auto-detects the language by default, or you can choose PowerShell, Python, Batch/cmd, Bash, JSON, JavaScript, TypeScript, HTML/XML, SQL, C#, YAML, Markdown, or Plain text. Colours follow the light/dark theme and appear in the read-only preview too — your saved template still stores plain text.
- **Long lines wrap instead of scrolling.** A long line now wraps to the next row rather than forcing a horizontal scrollbar, with the line numbers staying aligned, and PowerShell-style `-Arguments` are kept whole instead of breaking after the hyphen.
- **Auto-indent.** Enter keeps the current indentation and steps in after an opening `(` `[` `{` or a backtick; typing a closing `)` `]` `}` steps back out. Pasting multi-line code re-indents it to fit where you drop it.

{{CHANGELOG}}

### Browser extension

Pair Text Template Manager with the companion **TTM-Connect** extension to list and paste your templates from your browser:

- [Chrome Web Store](https://chrome.google.com/webstore/detail/jclopjpjdldbknjdhmjldehlkgbihlmi)
- [Microsoft Edge Add-ons](https://microsoftedge.microsoft.com/addons/detail/ttm-connect/fbpmhopmnaoedcnbegdkpfblekhaindm)
- [Firefox Add-ons](https://addons.mozilla.org/addon/ttm-connect/)

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
