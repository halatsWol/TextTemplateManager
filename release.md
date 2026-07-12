## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

### What's new in this release

- **Opt-in beta updates.** A new **Allow beta updates** switch in **Settings ▸ General** (off by default). With it on, pre-release (beta / preview) versions are offered too; with it off, only stable releases are.
- **Sturdier update detection.** The updater now handles pre-release and flexibly-formatted version tags — for example `v1.0`, `v1.0.11`, `v1.0.0.1`, `v0.9.6-beta`, and `v1.0.0-stable` — not just the strict `v1.0.0` format.
- **Quick Paste polish.** Right-click a template anywhere on its row to open the menu (not just on the text); **Alt+Esc** now cancels an in-progress multi-key shortcut instead of minimizing the window; the multi-key preview no longer marks a valid prefixed shortcut red; and the content preview keeps blank lines between paragraphs.
- **Shortcut conflicts move to the top-right.** The conflicts panel now sits in the top-right corner. When it shows only cross-area (sync ↔ local) notes it can be dismissed with its **×**; genuine same-area conflicts stay until you resolve them — and conflicts within a sync folder now surface as you edit.
- **Uppercase sync prefixes.** A synchronized folder's shortcut prefix (**Settings ▸ Sync**) is now always uppercase.
- **Illustrated handbook.** **Help ▸ Open Handbook** now includes screenshots walking through the main window, the editor, Quick Paste, and setting up synchronization.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Handbook**.
Source: https://github.com/halatsWol/TextTemplateManager
