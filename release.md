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

- **Keyboard navigation in Quick Paste and the tree.** The Quick Paste tree and shortcut lists are now fully keyboard-drivable: arrow keys move the selection, **← / →** expand and collapse folders, **Enter** or **Space** pastes a template (or expands a folder), and **Esc** steps back through focus (tree → search → shortcut list → close). Holding **Alt** now switches cleanly to multi-key entry. In the main window, **Enter** or **Space** also expands the selected folder.
- **"none" shortcut-prefix separator.** In **Settings ▸ Sync** you can now join a sync folder's prefix directly to a shortcut with no separator (for example `ANDMSG`), alongside `-` and `.`.

#### Fixes

- **Opening Quick Paste no longer collapses the main window's tree.**
- **The link dialog shows the existing text.** Editing a link with the cursor inside it now fills the Text field (it was left blank, and could replace the link's display text with the URL).

#### Changed

- The in-app handbook is now called the **Manual** (**Help ▸ Open Manual**).

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Manual**, or download
**`TextTemplateManager-Manual-{{VERSION}}.pdf`** from the assets below.
Source: https://github.com/halatsWol/TextTemplateManager
