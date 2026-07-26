; Inno Setup script — TextTemplateManager DELTA update installer.
; Installs ONLY the files changed since a specific previous version over an existing install, and
; deletes files removed since then. Guarded to refuse any base other than that exact version.
; Built by package-delta.ps1, which generates installer\delta-files.iss (the [Files]/[InstallDelete]
; lists) and compiles this with /DMyAppVersion=<to> /DFromVersion=<from>. Requires Inno Setup 6.3+.

#define MyAppName "TextTemplateManager"
#define MyAppPublisher "Marflow Software"
#define MyAppExeName "ttm.exe"

; TO version (what this delta upgrades to) and FROM version (the only base it may run on).
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef FromVersion
  #define FromVersion "0.0.0-dev"
#endif

[Setup]
; Same AppId and DefaultDirName as the full installer so the delta overlays the SAME install folder
; and the [Code] guard can read the full install's uninstall key.
AppId={{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}

PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes

; The delta only overlays the changed files onto an existing install — it must NOT own the uninstall
; entry. A normal Inno install rewrites the uninstall log + Add/Remove-Programs registry with only the
; files IT installs, so after a delta Windows showed the delta's ~8 MB (not the ~280 MB app) and
; uninstalling removed only the patched files. Uninstallable=no leaves the full install's unins000
; authoritative: it still lists every file and the real size, so uninstall removes the whole app.
; (DisplayVersion is refreshed on that existing key in [Registry] below. A file the delta ADDS that the
; base never logged is still removed on uninstall, because the full installer's [UninstallDelete] wipes
; the whole {app} folder.)
Uninstallable=no

SetupIconFile=Assets\AppIcon.ico

OutputDir=installer
OutputBaseFilename=TextTemplateManager-Update-{#FromVersion}-to-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

ChangesAssociations=yes

; Same silent-update behavior as the full installer: close the app, replace files, relaunch.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; The changed files ([Files]) and removed files ([InstallDelete]) for this specific delta.
#include "installer\delta-files.iss"

[Registry]
; The full install owns the Add/Remove-Programs entry (real size + uninstaller); the delta must not
; recreate it. Only refresh DisplayVersion on that existing key so Windows shows the new version. The
; leading "{{" is an escaped literal brace, so this targets ...\Uninstall\{9C4E7B2A-...}_is1 — the same
; key the [Code] guard reads. No .ttmdata association changes (paths are unchanged and the full install
; already registered them), and no uninsdelete flags, since this installer has no uninstaller of its own.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}_is1"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#MyAppVersion}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall

[Code]
// Refuse to run on any base other than FromVersion. Normal auto-update never trips this (the client
// only downloads a delta whose "from" matches its installed version); it's a backstop against an
// old first-.exe client grabbing the delta, and a clear message for a manual run on the wrong base.
function InitializeSetup(): Boolean;
var
  Installed: String;
begin
  Result := True;
  // Deltas only ever apply after the 1.x AppId migration (the migrating release is full-only), so the
  // base always uses the corrected AppId "{GUID}" and its uninstall key "{GUID}_is1".
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}_is1',
       'DisplayVersion', Installed) then
  begin
    if CompareText(Trim(Installed), '{#FromVersion}') <> 0 then
    begin
      MsgBox('This update applies to TextTemplateManager {#FromVersion}, but version ' + Installed +
             ' is installed. Please use the full installer.', mbCriticalError, MB_OK);
      Result := False;
    end;
  end
  else
  begin
    MsgBox('This is a delta update and requires TextTemplateManager {#FromVersion} to be installed. ' +
           'Please use the full installer.', mbCriticalError, MB_OK);
    Result := False;
  end;
end;
