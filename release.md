## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

### What's new in this release

#### Fixes

- **Synced files no longer churn (OneDrive conflict copies).** Opening the app read-only used to rewrite a shared sync file's root timestamp with each device's own value, so several devices sharing a file via OneDrive would fight over it and spawn `…-<device>.ttmdata` conflict copies. A read-only open now leaves the file completely untouched; it is written only when its contents actually change.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Handbook**.
Source: https://github.com/halatsWol/TextTemplateManager
