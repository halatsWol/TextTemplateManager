## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed. The bundled runtime is always the latest .NET 8 release available at the time this version was built.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

> **Note — Remote Desktop (RDP):** If Text Template Manager runs on both your local computer and a remote computer you connect to over RDP, the global Quick Paste hotkey always opens Quick Paste on the **local** computer — Windows delivers a registered global hotkey locally, so it never reaches the remote session. Give each machine a **different** Quick Paste hotkey, or run only one instance, to avoid the clash.

### What's new in this release

#### New & improved

- **Copy from read-only synced templates.** When a synced folder has saving turned off, you can now select and copy text out of its templates — the content stays read-only (no editing, typing, or pasting into it).
- **Click anywhere on a row.** In the template trees and the Quick Paste shortcut lists, clicking anywhere on a row selects it — not just the text label.
- **Clearer resize handles.** The draggable dividers between panes now show a gripper so it's obvious you can drag them to resize.
- **Tidier pastes.** Blank lines at the very top of a template are trimmed when it's pasted.

#### Fixes

- **Double-click paste in Quick Paste works again.** Double-clicking a template — in the tree or the shortcut list — now pastes it into your app, instead of the content landing in the Quick Paste search box.
- **Read-only synced folders stay read-only.** When a synced folder has saving turned off, you can no longer add or delete templates and folders inside it.

#### Security

- **Stricter browser-connector content filtering.** Content created from — and served back to — the companion browser extension is now cleaned with a strict allow-list plus a deny-list hardening pass: only safe formatting survives, scripts and other active content are removed, and links are locked down. Code blocks are preserved intact. See the manual for the full list of what's kept and removed.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
