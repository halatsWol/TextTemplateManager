# Text Template Manager

A hotkey-driven text-template paste tool for Windows. Organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

> Made by **Marflow Software**. Built with WinUI 3 (Windows App SDK) and .NET 8.

---

## Features

- **Rich templates** — formatted text (bold, colors, lists, tables, headings) with automatic save.
- **Quick Paste** — press a global hotkey anywhere; paste by **single key** or by holding **ALT** and
  typing a **multi-key** shortcut.
- **Folders & drag-drop** — organize templates; a template can never contain other items.
- **Sync** — share a `.ttmdata` file across machines via a cloud folder (e.g. OneDrive); shown as a
  pinned folder, with per-source read-only and shortcut-prefix options.
- **Area-aware shortcuts** — the same key can be reused across sync folders; it resolves by priority
  (local first, then sync folders in order).
- **Paste modes** — Auto, Plaintext, Markdown, RTF, HTML.
- **Backup / export** — export the whole tree or a single folder.
- **Run at login**, configurable global hotkey, and an in-app **Handbook** (PDF).

---

## Install

Download the latest `TextTemplateManager-Setup-<version>.exe` from the
[Releases](https://github.com/marflow-software/text-template-manager/releases) page and run it.

It is a **per-user** install (no administrator rights) and **self-contained** — the .NET 8 runtime
and the Windows App SDK ship inside the app, so nothing else needs to be installed.

---

## Build from source

Prerequisites:

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (builds the bundled TipTap editor)
- Windows 10 19041+ / Windows 11

```powershell
# Restore & build (x64 is required)
dotnet build "Marflow Software - TextTemplateManager.csproj" -c Debug -p:Platform=x64
```

Run/debug from Visual Studio (which deploys the Windows App SDK runtime). A loose Debug `ttm.exe`
run directly will not start, because the runtime is only registered by a deploy or a self-contained
publish.

### The handbook

`docs/Handbook.md` is rendered to `Handbook.pdf` (next to the exe) at build time by the small
`tools/HandbookGen` tool. Edit the markdown and rebuild to regenerate it. If the tool can't run
(e.g. offline), the last committed `Assets/Handbook.pdf` is used.

---

## Package an installer locally

Requires [Inno Setup 6.3+](https://jrsoftware.org/isdl.php) in addition to the build prerequisites.

```powershell
# Version from the latest git tag, or 0.0.0-dev if none
pwsh -File package.ps1

# ...or pass an explicit version
pwsh -File package.ps1 -Version 0.9.3
```

This publishes a self-contained build to `publish/win-x64` and produces
`installer/TextTemplateManager-Setup.exe`.

---

## Releasing

Releases are automated. **Pushing a version tag** builds the installer and publishes a GitHub
release (see [`.github/workflows/release.yml`](.github/workflows/release.yml)):

```bash
git tag v0.9.3
git push origin v0.9.3
```

The tag (without the leading `v`) becomes the **single source of the version** — it is written into
the assembly (shown in **Help ▸ About**), the file version, and the installer. Local builds without
a tag report `0.0.0-dev`.

---

## Tech stack

- **UI** — WinUI 3 / Windows App SDK 1.8, MVVM (CommunityToolkit.Mvvm)
- **Editor** — TipTap / ProseMirror, bundled with esbuild, hosted in WebView2
- **Storage** — `System.Text.Json` (`.ttmdata` files)
- **Handbook** — Markdig + QuestPDF (`tools/HandbookGen`)
- **Installer** — Inno Setup (unsigned, per-user)

---

## License

Copyright © Marflow Software. All rights reserved.
