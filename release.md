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

#### New & improved

- **Quick Paste moves under the keyboard more naturally.** In the search box the **↑ / ↓** keys move the text cursor, and pressing **↓** once it's at the end (or the box is empty) steps into the template tree — from there **Esc** returns to the box with your text intact. **Tab** now moves cleanly between the search box, the tree and the shortcut list, and the highlighted row in the shortcut list shows only while it's the active area.
- **Shortcut mode is just for shortcuts now.** With the search box unfocused, a key that isn't a template shortcut sounds a short tone instead of quietly starting a search — so a key you're used to doing nothing can't suddenly start pasting once a shortcut is assigned to it. **Shift**, **Caps Lock** and **Ctrl** no longer pull focus away either.
- **Longer multi-key shortcuts.** A multi-key shortcut can now be up to 20 characters (previously 15).

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
