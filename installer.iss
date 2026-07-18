; Inno Setup script — TextTemplateManager
; Produces an UNSIGNED, per-user installer (no code-signing certificate required to install).
; Build the payload first with package.ps1 (publishes an unpackaged, self-contained app to
; publish\win-x64), then this script wraps it into installer\TextTemplateManager-Setup.exe.
; Requires Inno Setup 6.3+ (for the x64compatible architecture token).

#define MyAppName "TextTemplateManager"
#define MyAppPublisher "Marflow Software"
; Version comes from the release tag via ISCC /DMyAppVersion=X.Y.Z (package.ps1 -Version X.Y.Z
; passes it, and the GitHub release workflow derives it from the tag). This default is only used
; for a direct local ISCC run and marks it as a non-release build.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
; The published executable (AssemblyName in the .csproj).
#define MyAppExeName "ttm.exe"

[Setup]
AppId={{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}

; --- Per-user install, no administrator rights ---
; Installs to %LocalAppData%\Programs\Marflow Software\TextTemplateManager and uses the
; per-user Start Menu.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DisableProgramGroupPage=yes

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Shown on the License Agreement page during interactive install (skipped when silent).
LicenseFile=LICENSE

; The installer .exe uses the app icon.
SetupIconFile=Assets\AppIcon.ico

OutputDir=installer
OutputBaseFilename=TextTemplateManager-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Refresh Explorer's icon/association cache after the .ttmdata registry keys change.
ChangesAssociations=yes

; Auto-update runs Setup silently. Let it close the running app so files can be replaced; the
; [Run] entry (no skipifsilent) relaunches the app when the silent install finishes.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start {#MyAppName} automatically when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Auto-start on login (current user). Added only when the task is selected; removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

; Associate .ttmdata with the app (per-user; no admin). Opening one launches ttm.exe with the file
; path, which links it as a sync source (see App.OnLaunched -> MainPage.HandleOpenTtmDataFile).
Root: HKCU; Subkey: "Software\Classes\.ttmdata"; ValueType: string; ValueName: ""; ValueData: "TextTemplateManager.ttmdata"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata"; ValueType: string; ValueName: ""; ValueData: "Text Template Manager data"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall
