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

#### Fixes

- **↓ from the search box enters the tree.** In the main window, pressing **↓** in the search box now moves into the template tree, the same way as in Quick Paste: if the text cursor isn't at the end yet, the first **↓** moves it there, the next one into the tree.
- **Esc in the tree returns to the search box.** **Esc** now steps back one level at a time: from the tree to the search box (your search text is kept), then it clears the search text, and with the search box already empty it clears the selection, as **Esc** in the tree did before.

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
