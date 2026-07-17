## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed. The bundled runtime is always the latest .NET 8 release available at the time this version was built.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

### What's new in this release

#### Fixes

- **Default paste mode now applies to new templates.** Creating a template uses the mode set in **Settings ▸ General ▸ Default paste mode** again, instead of always starting on Auto.
- **Opening Quick Paste no longer writes to synced files.** The first time the Quick Paste window opened in a session, it rewrote each active sync file even when nothing was pasted — enough to set OneDrive syncing and, across several devices, spawn conflict copies. Opening the window now leaves synced files untouched.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
