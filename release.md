## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

### What's new in this release

- **Quick Paste keyboard navigation.** The single-key and multi-key lists now highlight a row you can move with **↑ / ↓** and paste with **Enter**. For multi-key, a partial entry is enough: the list highlights the top match as you type, and releasing **Alt** pastes it — or press **Enter** to paste the highlight even before typing. The highlight follows what you type, and deleting the whole entry cancels the paste.

#### Fixes

- **Synced files no longer churn (OneDrive conflict copies).** Opening the app read-only used to rewrite a shared sync file's root timestamp with each device's own value, so several devices sharing a file via OneDrive would fight over it and spawn `…-<device>.ttmdata` conflict copies. A read-only open now leaves the file completely untouched; it is written only when its contents actually change.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Handbook**, or download
**`TextTemplateManager-Handbook-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
