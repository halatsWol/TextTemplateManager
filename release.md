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

- **Reworked update experience.** Updates now tell you where you can actually see them. The top-right corner shows the current state — checking, downloading, or ready to install — and the **Check for Updates** menu entry carries a small marker while one is waiting. When the window is closed to the tray, you get a Windows notification instead, with **Install now** on it.
- **Install updates automatically.** A new option under **Settings ▸ General ▸ Updates**. Off by default, so nothing changes unless you turn it on. With it on, a downloaded update installs on its own at a genuinely quiet moment — a few minutes with no keyboard or mouse activity, no Quick Paste window open, and nothing being saved — or otherwise the next time the app starts. It never interrupts you mid-task, and the app returns to the tray afterwards if that is where it was.
- **Settings reorganised.** **Startup** and **Updates** are now separate sections rather than one mixed list.
- **Release date in About.** **Help ▸ About** now shows the date the version was built.

#### Fixes

- An update can no longer install while data is being written, which could leave a synchronized file half-written or produce a cloud conflict copy.
- Closing the window to the tray now leaves the Settings view, so reopening from the tray lands on your templates instead of resuming in Settings.
- The Administrator & Deployment Guide PDF now shows its command examples, which were previously dropped.

{{CHANGELOG}}

### Browser extension

Pair Text Template Manager with the companion **TTM-Connect** extension to list and paste your templates from your browser:

- [Chrome Web Store](https://chrome.google.com/webstore/detail/jclopjpjdldbknjdhmjldehlkgbihlmi)
- [Microsoft Edge Add-ons](https://microsoftedge.microsoft.com/addons/detail/ttm-connect/fbpmhopmnaoedcnbegdkpfblekhaindm)
- [Firefox Add-ons](https://addons.mozilla.org/addon/ttm-connect/)

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
