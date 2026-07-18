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

This is the first **stable** release after the 0.9 beta series.

#### New

- **Open `.ttmdata` files directly.** Double-clicking a `.ttmdata` file (or **Open with ▸ Text Template Manager**) opens the app on **Settings ▸ Sync** and links the file as a sync source. If it is already linked it is just shown, and the app's own data file is never added.
- **Set the default app for `.ttmdata`.** A **Set as default for .ttmdata** button in **Settings ▸ General ▸ File association** re-registers the association if another program has taken it over.
- **Follow links from the editor.** **Ctrl+click** a link in the template editor to open it in your default browser, and hover a link to see where it points. A plain click still just places the cursor for editing.

#### Changed

- **Single instance.** Opening a file — or launching the app again — now brings the running window to the front instead of starting a second copy.
- **`.ttmdata` everywhere.** Adding a sync source and saving or loading backups now use the `.ttmdata` format only, and the Sync **+** button is now labelled **Add**. An older `.json` backup still loads after you rename it to `.ttmdata` (the format is identical).
- **App-data folder renamed** to `…\Marflow Software\TextTemplateManager`; your existing templates, settings, and sync configuration are moved there automatically on first launch.

#### Fixes

- **Editor callout panels and code blocks no longer trap the cursor.** Press **↓** or **→** at the end of a panel or code block, or **Enter** on an empty last line, to step back out — matching Jira's behavior.
- **The text-color and highlight buttons now show when they are active,** including formatting you turn on before typing with nothing selected.
- **The "none" shortcut-prefix separator** is no longer clipped in the Sync dropdown.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
