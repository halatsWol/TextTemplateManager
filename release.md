## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

### What's new in this release

- **Enterprise update policy.** Administrators can restrict updates via a read-only registry value — `allowUpdate` (DWORD) under `Software\MarflowSoftware\TextTemplateManager` in HKLM or HKCU: `0` allows everything, `1` blocks beta only, `2` disables updates entirely. The affected switches are turned off and grayed out, with a note explaining the policy. See *Updates* in the handbook.
- **"Saved" indicator.** The main window now briefly shows a **Saved** confirmation whenever your templates are written to disk.
- **Live shortcut capture.** Setting the global Quick Paste hotkey in **Settings ▸ General** now shows each key as you press it.

### Fixes

- **Per-template undo history.** Undo in the editor no longer reaches back into a previously selected template — each template starts with a clean history, with its own content as the first entry.
- **Search reveals its matches.** Searching in the main window now expands every folder and subfolder that contains a match.
- **Quick Paste search.** The Quick Paste search box now filters to matching templates instead of listing everything.
- **Search resets on close.** Closing the main window clears the search box, so it reopens unfiltered.
- **Autostart no longer forced on.** Installing without the autostart option no longer switches *Run at Windows login* on by itself.
- **Hotkey capture.** Recording the global shortcut in settings no longer opens Quick Paste or drops the final key.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Handbook**.
Source: https://github.com/halatsWol/TextTemplateManager
