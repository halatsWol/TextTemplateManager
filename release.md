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

#### TextTemplateManager 2 is available in the Microsoft Store

TextTemplateManager 2 is the new version of this app. It installs and updates through the [Microsoft Store](https://apps.microsoft.com/detail/9NMBSZLLLN6V) and brings your templates, settings and sync folders along: at its first start it backs them up and offers to import them. This is the last feature release of version 1; from now on it only gets important fixes.

- **Notice at every start.** A dialog points to TextTemplateManager 2, with **Open in Store** (the Microsoft Store app), **Open in browser** (the Store page in your default browser) and **Later**. Tick **Don't show this again** to stop it. Until then, a start hidden in the system tray at Windows sign-in opens the window, so the notice isn't missed. The same two links are under **Help ▸ About**.
- **Steps aside once TextTemplateManager 2 is installed.** This version then no longer registers the Quick Paste shortcut or starts the browser connector, so it never competes with version 2. When you open it, it offers to **Remove** this version (your data stays in place for version 2), to **Open in TTM 2**, or **Later**. Started hidden at Windows sign-in, it quietly exits.

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
