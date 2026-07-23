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

#### New

- **Use the same shortcut in more than one area.** The same single- or multi-key shortcut can now be used in both your local templates and synchronized folders (or across several sync folders). Quick Paste resolves it by priority — the local template first, then synchronized folders in order — and the shortcut list shows every match, local first. Multi-key shortcuts are compared on their effective (prefixed) form, so different folder prefixes still keep them apart.
- **Hide cross-area shortcut warnings** (**Settings ▸ General**, off by default) suppresses the dismissible note about a shortcut being reused across areas. Blocking same-area conflicts are always shown.

#### Changed

- **Quick Paste opens instantly.** The window is kept ready between uses (and prepared shortly after launch), so it appears immediately and is ready for a single-key shortcut right away. Closing it hides it rather than tearing it down.
- **Sync settings:** the reorder arrows are disabled at the ends of the list — the top source can't move up and the bottom can't move down.
- **Smaller updates from 1.2 onward.** When you update from the version right before a release, the app can fetch a compact *delta* update — only what changed — instead of the whole installer, when one is available; larger version jumps use the full installer. The full installer is always available on the Releases page. (The size benefit begins with the first update after 1.2.)

#### Fixes

- **Reliable autosave in the Title, Tags, and shortcut fields** — edits are saved a short moment after you stop typing, the same as the editor.
- **Fixed a crash when reordering synchronized sources.**
- **Hardened synchronization against rapid edits** — quickly reordering sources, or making several changes at once, no longer collides on the settings file or crashes.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
