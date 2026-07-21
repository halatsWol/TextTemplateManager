# Text Template Manager

A hotkey-driven text-template paste tool for Windows. Organize reusable snippets in folders,
give them shortcuts, and paste them into any application from a global Quick Paste window.

> Made by **Marflow Software**. Built with WinUI 3 (Windows App SDK) and .NET 8.

---

## Background

I started this project because I was never quite happy with the text-template tools I could
find. Most had only minimalistic feature sets, felt outdated overall, or had dated, bare-bones
designs — and the ones that came close usually only covered *some* of what I wanted. The biggest
deal breaker was always the lack of **sync** and Separation, so a team could share the same templates file but only use those they need.

This is my **first C#/WinU 3/.NET project**. It has been in the works for about a year (started 2025-07-15)
and was thrown away and rebuilt from scratch several times along the way. I'm not a professional
software developer and don't aim to be — I work in IT but got frustrated sufficiently by available tools that I took it on me to solve this issue. AI was used occasionally (mainly for
debugging complex issues, planning some structures, and the like); any code written with or by AI
was always reviewed and double-checked by me.

---

## Features

- **Rich templates** — formatted text (bold, colors, lists, tables, headings) with automatic save.
- **Quick Paste** — press a global hotkey anywhere; paste by **single key** or by holding **ALT** and
  typing a **multi-key** shortcut.
- **Folders & drag-drop** — organize templates; a template can never contain other items.
- **Sync** — share a `.ttmdata` file across machines via a cloud folder (e.g. OneDrive); shown as a
  pinned folder, with per-source read-only and shortcut-prefix options. Open a `.ttmdata` file in
  Explorer to add it as a source.
- **Area-aware shortcuts** — the same key can be reused across sync folders; it resolves by priority
  (local first, then sync folders in order).
- **Paste modes** — Auto, HTML/Jira, HTML, RTF, Markdown, Plaintext. Callout panels adapt to each mode: native panels in Jira, colored boxes in HTML/RTF, a labeled quote in Markdown.
- **Backup / export** — export the whole tree or a single folder.
- **Auto-update** — checks GitHub Releases and installs a newer version silently (opt-out in settings).
- **Browser connector (beta)** — an opt-in local API a companion browser extension ([TTM-Connect](https://github.com/halatsWol/TTM-Connect)) can call to list, paste, and create templates ([API docs](docs/BrowserConnectorApi.md)). Available on:
  - [Chrome Web Store](https://chrome.google.com/webstore/detail/jclopjpjdldbknjdhmjldehlkgbihlmi)
  - [Firefox Add-ons](https://addons.mozilla.org/addon/ttm-connect/)
  - Microsoft Edge — coming soon
- **Run at login**, configurable global hotkey, and an in-app **Manual** (PDF).

---

## Install

Download the latest `TextTemplateManager-Setup-<version>.exe` from the
[Releases](https://github.com/halatsWol/TextTemplateManager/releases) page and run it.

It is a **per-user** install (no administrator rights) and **self-contained** — the .NET 8 runtime
and the Windows App SDK ship inside the app, so nothing else needs to be installed. The bundled
runtime is always the latest .NET 8 release available at the time the version was built.

> **Note — Remote Desktop (RDP):** If Text Template Manager runs on both your local computer and a
> remote computer you connect to over RDP, the global Quick Paste hotkey always opens Quick Paste on
> the **local** computer — Windows delivers a registered global hotkey locally, so it never reaches
> the remote session. Give each machine a **different** Quick Paste hotkey, or run only one instance,
> to avoid the clash.

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

### The manual

`docs/Manual.md` is rendered to `Manual.pdf` (next to the exe) at build time by the small
`tools/ManualGen` tool. Edit the markdown and rebuild to regenerate it. If the tool can't run
(e.g. offline), the last committed `Assets/Manual.pdf` is used.

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

The workflow creates the release as a **draft** with the installer attached and notes rendered from
[`release.md`](release.md); review it on the Releases page and click **Publish release** when ready.
The in-app auto-update only sees *published* releases, so users are not offered the update until you
publish.

---

## Tech stack

- **UI** — WinUI 3 / Windows App SDK 1.8, MVVM (CommunityToolkit.Mvvm)
- **Editor** — TipTap / ProseMirror, bundled with esbuild, hosted in WebView2
- **Storage** — `System.Text.Json` (`.ttmdata` files)
- **Manual** — Markdig + QuestPDF (`tools/ManualGen`)
- **Installer** — Inno Setup (unsigned, per-user)

---

## License

Source-available under a custom license — see [LICENSE](LICENSE). In short: free to use, run,
copy, and modify for any purpose that complies with the terms (including inside a company), but
**not** for sale and **not** to be provided as, or as part of, a service — directly or indirectly,
and not by modifying it first.

Copyright © 2026 Marflow Software.
