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

#### Safer data

- **Changes are saved before the app closes.** Exiting from **File ▸ Exit** or the tray icon first saves everything still pending, including the last few keystrokes in the editor, and the app now also saves when you sign out of Windows or shut down. If a synced file is slow to respond, a "Saving changes…" message appears instead of closing early.
- **A damaged data file is never overwritten.** If your templates or settings can't be read at startup, the file is kept as `….broken-<date-time>` next to the original, the app tells you, and it starts without it — instead of later replacing the file with an empty one.
- **Settings are written safely.** Settings and sync settings are now saved the same crash-proof way as your templates, so an interrupted save can't leave a half-written file behind.
- **Load Backup keeps your synced folders.** Loading a backup now asks first, replaces only your local templates and folders, and leaves synced folders alone. A file that can't be read is reported instead of silently ignored.
- **Saving no longer pauses during shortcut conflicts.** Moving templates or exiting while two templates share a shortcut now saves as usual; the conflict stays visible in the warning panel.

#### Fixes

- **Digit shortcuts work in Quick Paste.** A single-key shortcut on a digit (top row or numpad) now pastes; before, only letters did.
- **RTF pastes keep code and special characters.** Code blocks keep their line breaks and are set in a monospace font, characters such as `€`, `Ω`, Cyrillic or emoji arrive intact, and a paste no longer starts with an empty line.
- **Shortcuts are letters and digits only.** The shortcut fields now accept exactly what is stored, so what you type is what you get. `-` and `.` remain the separator between a synced folder's prefix and its shortcuts.
- **Read-only synced templates are read-only for the keyboard too.** Their title, shortcut, tag and paste-mode fields can no longer be changed by tabbing into them; text can still be selected and copied.
- **Hotkey clashes are reported.** If another app already uses the Quick Paste shortcut, the app now says so at startup and in **Settings ▸ General**, instead of Quick Paste silently not opening.
- **More robust update check.** Only the release's `TextTemplateManager-Setup` installer is ever run as an update, and the check looks further back through the release list when needed.

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
