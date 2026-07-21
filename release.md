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

- **Browser extension now on Firefox Add-ons.** The companion extension ([TTM-Connect](https://github.com/halatsWol/TTM-Connect)) is now available for Firefox as well:
    - [Chrome Web Store](https://chrome.google.com/webstore/detail/jclopjpjdldbknjdhmjldehlkgbihlmi)
    - [Firefox Add-ons](https://addons.mozilla.org/addon/ttm-connect/)
    - Microsoft Edge — coming soon
- **Create templates from the browser.** With the connector enabled, the companion extension can save selected text as a new template — it is added to your local templates.

#### Changed

- **Help ▸ About** now shows the connector API protocol version.

#### Fixes

- **Single-key shortcuts no longer fire while you're typing in the Quick Paste search box.** The window opens ready for shortcuts, but once the search box (or the tree) has focus, letters only filter the list — the first letter of a search is no longer swallowed as a paste.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
