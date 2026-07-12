## Text Template Manager {{VERSION}}

A hotkey-driven text-template paste tool for Windows — organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

### Install

Download **`TextTemplateManager-Setup-{{VERSION}}.exe`** from the assets below and run it.

- Per-user install — no administrator rights required.
- Self-contained — the .NET 8 runtime and the Windows App SDK are bundled, so nothing else needs to be installed.

Already have it installed? The app offers this update automatically, or you can trigger it from **Help ▸ Check for Updates**.

### What's new in this release

- **Callout panels in the editor.** A new **Panel** toolbar button wraps text in a colored **Info**, **Note**, **Success**, **Warning**, or **Error** callout — the same styles Atlassian Jira uses.
- **Panels paste everywhere.** Panels adapt to each paste mode: native panels in Jira with **HTML/Jira**, colored boxes with **Auto**, **HTML**, and **RTF** for email and word processors, and a labeled quote with **Markdown**.
- **Paste-mode menus reordered** to most-used first: Auto, HTML/Jira, HTML, RTF, Markdown, Plaintext.
- **Sync folder names are read-only in the tree.** A pinned sync folder's name is set only in **Settings ▸ Sync** and can no longer be changed (or accidentally edited) from the tree.
- **Clear the selection easily.** Click an empty area of the tree, or press **Esc**, to deselect — so new templates and folders are created at the root instead of inside the selected folder.
- **Move to Root.** Right-click any nested item and choose **Move to Root** to move it out to the top level — a reliable alternative to dragging.
- **Drop rules.** Dropping a template onto another template now places it as a sibling directly below it; templates never contain other items.

{{CHANGELOG}}

---

Full documentation is available in the app under **Help ▸ Open Handbook**.
Source: https://github.com/halatsWol/TextTemplateManager
