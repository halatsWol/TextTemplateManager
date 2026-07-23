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
; Same AppId as the full installer, so it updates the same install + uninstall entry (and bumps
; its DisplayVersion to MyAppVersion).
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

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

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
; Re-assert the .ttmdata association (idempotent; path is unchanged). No autostart task here — the
; app manages its own login entry, and existing shortcuts from the full install are left in place.
Root: HKCU; Subkey: "Software\Classes\.ttmdata"; ValueType: string; ValueName: ""; ValueData: "TextTemplateManager.ttmdata"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata"; ValueType: string; ValueName: ""; ValueData: "Text Template Manager data"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

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
