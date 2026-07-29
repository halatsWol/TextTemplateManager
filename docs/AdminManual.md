## Overview

This guide covers the administrator- and deployment-facing details of Text Template Manager that
everyday users do not need: silent/scripted installation, centrally controlling updates, the registry
and file-system footprint, and the standalone cleanup utility. End-user documentation is in the
**User Manual** (in the app under **Help > Open Manual**).

Text Template Manager is a **per-user** application: it installs under the user's profile, writes only
to `HKEY_CURRENT_USER`, and needs no administrator rights to install, update, or uninstall. The one
exception is the cleanup utility's `/user` option (below), which requires elevation.

---

## Installation

The installer is an Inno Setup executable, `TextTemplateManager-Setup-<version>.exe`.

- Install location: `%LocalAppData%\Programs\Marflow Software\TextTemplateManager`
- Self-contained: the .NET 8 runtime and the Windows App SDK are bundled; nothing else is required.
- Registration is per-user (HKCU); no elevation is requested or needed.

### Command-line options

The Setup executable accepts the standard Inno Setup switches. The most useful for deployment:

| Switch | Effect |
| --- | --- |
| `/SILENT` | Install with a progress window but no prompts. |
| `/VERYSILENT` | Install with no UI at all. |
| `/SUPPRESSMSGBOXES` | Suppress message boxes (use together with a silent switch). |
| `/NORESTART` | Never restart the machine. |
| `/NOCANCEL` | Prevent cancelling during a silent install. |
| `/DIR="path"` | Override the install directory. |
| `/TASKS="list"` | Select optional tasks (comma-separated): `autostart`, `desktopicon`. |
| `/MERGETASKS="list"` | Like `/TASKS` but merges with the defaults instead of replacing them. |
| `/LOG="file"` | Write an installation log to the given file. |
| `/LANG=english` | Set the setup language. |
| `/?` | Show the full built-in list of switches. |

Example — silent install with autostart enabled:

    TextTemplateManager-Setup-1.3.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="autostart"

Notes:

- The optional tasks (`autostart`, `desktopicon`) are **off by default**.
- On a **fresh** silent install, `/TASKS="autostart"` enables autostart. On a silent **upgrade** of an
  existing install the current autostart on/off state is preserved and `/TASKS` is ignored, so an
  auto-update never changes the user's choice.
- The in-app auto-updater runs the installer as `/SILENT /SP- /NOCANCEL /NORESTART /SUPPRESSMSGBOXES`.

---

## Autostart

"Run at Windows login" is stored entirely in the registry (nothing in the app's settings file), using
the same two keys Windows' own Startup Apps UI uses:

| Registry value | Purpose |
| --- | --- |
| `HKCU\...\CurrentVersion\Run` value `TextTemplateManager` | The command line launched at sign-in. |
| `HKCU\...\Explorer\StartupApproved\Run` value `TextTemplateManager` | Enabled/disabled flag: first byte `0x02` = enabled, `0x03` = disabled; absent = enabled. |

- The Run value is `"<install>\ttm.exe"`, optionally followed by `--hidden`.
- **`--hidden`** launches the app minimized to the system tray at sign-in (the Quick Paste hotkey and
  tray icon still work). The in-app setting *Start hidden in the system tray* adds or removes this flag.
- Because the choice lives in these keys, it stays in sync with Windows' Startup Apps toggle and is
  remembered across reinstalls.

---

## File association

The `.ttmdata` backup/sync format is associated per-user:

- `HKCU\Software\Classes\.ttmdata` points at the `TextTemplateManager.ttmdata` ProgId
- `HKCU\Software\Classes\TextTemplateManager.ttmdata` (with `DefaultIcon` and `shell\open\command`)

---

## Update delivery

The app checks GitHub Releases and updates itself in place. Each release publishes the full installer
`TextTemplateManager-Setup-<version>.exe`, a `manifest.json` (SHA-256 of every installed file), an
`update.json` (version, full-installer name, and any delta), and — when applicable — a **delta**
installer `TextTemplateManager-Update-<from>-to-<to>.exe` containing only the files changed since the
immediately previous release.

A client updating from exactly the previous version takes the smaller delta when one is available;
larger version jumps, or a release without a matching delta, fall back to the full installer. A
release that bundles a refreshed .NET runtime produces a larger delta (the whole runtime changed) —
this is expected and not itemised in the release notes.

### Controlling updates by policy

Updates can be restricted centrally with a registry value the application only ever **reads** (it
never writes it), so it can be enforced by Group Policy or a deployment script and users cannot change
it in the app. Set a `DWORD` named `allowUpdate` under `Software\MarflowSoftware\TextTemplateManager`
in `HKEY_LOCAL_MACHINE` (machine-wide) or `HKEY_CURRENT_USER` (per-user):

| Value | Effect |
| --- | --- |
| `0` (or absent) | Updates and beta updates allowed — normal behavior. |
| `1` | Stable updates allowed; beta updates blocked (the *Allow beta updates* switch is off and disabled). |
| `2` | All update checks disabled (the *Automatically check for updates* switch is off and disabled; no check runs). |

`HKEY_LOCAL_MACHINE` takes precedence over `HKEY_CURRENT_USER`. When a policy is in effect, a note
appears under the update switches in **Settings > General**. This restricts only the built-in updater;
it does not prevent installing a newer version by hand.

---

## Cleanup / force-uninstall utility

`TextTemplateManager-CleanupUtility.exe` completely removes an installation **without** the normal
uninstaller — useful after a corrupted install or uninstall, or to script removal. It is bundled in
the install folder and also shipped as a standalone release asset. For ordinary removal, users should
still use **Settings > Apps** (Add/Remove Programs); this tool is the force option.

It force-closes `ttm.exe` (and its WebView2 processes), restarts `TextInputHost`, then removes the
install folder, shortcuts, and all registry entries — autostart, file association, and **both** the
current and the older mis-registered uninstall keys. User data is **kept** unless you opt in.

Run it with no arguments for a small window, or with options:

| Option | Effect |
| --- | --- |
| `/force` | Proceed without the confirmation prompt. |
| `/whatif` | Show what would be removed; change nothing. |
| `/quiet` | No window and no console output (log file only). Requires `/force`. |
| `/nonewwindow` | No window; show a progress bar in the console. |
| `/keeptextinputhost` | Do not restart TextInputHost. |
| `/removesettings`, `/removesync`, `/removetemplates` | Also delete the named user data. |
| `/removeall` | Also delete all user data. |
| `/user:NAME` | Clean another local user's installation. Requires **administrator**. |
| `/log:PATH` | Log file, or a folder (a trailing backslash also means a folder) that gets a default filename. Default location: `%LocalAppData%\Programs\Marflow Software`. |
| `/loglevel:LEVEL` | Log detail: `normal`, `verbose`, or `debug`. |
| `/?`, `/h`, `/help` | Show usage. |

Options accept `/x`, `-x`, or `--x`. If a file is locked by a still-running process it is reported so
you can restart the device and run the tool again. In a remote or non-interactive session `/force` is
required, because there is no interactive session to confirm against.

Example — silent full removal including all data, for the current user:

    TextTemplateManager-CleanupUtility.exe /force /quiet /removeall

Example — an administrator cleaning another user's installation:

    TextTemplateManager-CleanupUtility.exe /user:jdoe /force

---

## Registry and file-system reference

All application registry state is under `HKEY_CURRENT_USER` (per install user):

| Location | Contents |
| --- | --- |
| `...\CurrentVersion\Uninstall\{9C4E7B2A-...}_is1` | Add/Remove Programs entry (uninstaller, version, size). |
| `...\CurrentVersion\Run` value `TextTemplateManager` | Autostart command line. |
| `...\Explorer\StartupApproved\Run` value `TextTemplateManager` | Autostart enabled/disabled flag. |
| `Software\Classes\.ttmdata` and `Software\Classes\TextTemplateManager.ttmdata` | File association. |
| `Software\MarflowSoftware\TextTemplateManager` value `allowUpdate` | Update policy (read-only; may also be set in HKLM). |

File-system locations:

| Path | Contents |
| --- | --- |
| `%LocalAppData%\Programs\Marflow Software\TextTemplateManager` | The application. Removed on uninstall. |
| `%LocalAppData%\Marflow Software\TextTemplateManager` | User data: `data.ttmdata`, `settings.ttmsettings`, `sync.ttmsettings`, staged update installers, `crash.log`. Kept on uninstall. |
