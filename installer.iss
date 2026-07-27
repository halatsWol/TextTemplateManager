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
AppId={{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}
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
; Auto-start on login. The Run value and the StartupApproved enabled/disabled flag are both written in
; [Code] below (CurStepChanged) so a silent auto-update can't clobber a user's hidden-start choice or their
; current on/off state. This line writes nothing at install (dontcreatekey) — it only schedules the Run value
; for deletion on uninstall. StartupApproved is deliberately left in place so a reinstall remembers the choice.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#MyAppName}"; Flags: dontcreatekey uninsdeletevalue

; Associate .ttmdata with the app (per-user; no admin). Opening one launches ttm.exe with the file
; path, which links it as a sync source (see App.OnLaunched -> MainPage.HandleOpenTtmDataFile).
Root: HKCU; Subkey: "Software\Classes\.ttmdata"; ValueType: string; ValueName: ""; ValueData: "TextTemplateManager.ttmdata"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata"; ValueType: string; ValueName: ""; ValueData: "Text Template Manager data"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\TextTemplateManager.ttmdata\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[UninstallDelete]
; Remove the entire install folder on uninstall, not just the files in the uninstall log. A delta
; update overlays files without touching this uninstaller's log, so a file a delta ADDS wouldn't
; otherwise be logged for removal — this wipes the whole folder so nothing is left behind. Safe because
; {app} holds only the app payload; user data lives in %LocalAppData%\Marflow Software (a separate tree).
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall

[Code]
// One-time AppId migration. Releases up to 1.2 were built with a malformed AppId ("{GUID}}" — an
// extra closing brace, from writing {{...}} in [Setup]), so they registered under the uninstall key
// "{GUID}}_is1". This release uses the corrected AppId "{GUID}". Before installing, silently uninstall
// any lingering old "{GUID}}" install so the user isn't left with two entries. Templates/settings live
// in %LocalAppData%\Marflow Software\TextTemplateManager (a separate folder) and are NOT touched.
const
  OldUninstKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C4E7B2A-1F53-4A8D-B6E0-3D7C2F9A15E4}}_is1';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  StartupValueName = '{#MyAppName}';

var
  gTasksInit: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninst: String;
  ResultCode, Tries: Integer;
begin
  Result := '';   // best effort — never block the install on the migration
  if RegQueryStringValue(HKCU, OldUninstKey, 'UninstallString', Uninst) and (Uninst <> '') then
  begin
    Uninst := RemoveQuotes(Uninst);
    if FileExists(Uninst) then
    begin
      Exec(Uninst, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      // The Inno uninstaller relaunches from %temp% and returns early, so wait (up to ~30s) for the
      // old uninstall key to disappear before the new files are written to the same folder.
      Tries := 0;
      while (Tries < 60) and RegKeyExists(HKCU, OldUninstKey) do
      begin
        Sleep(500);
        Tries := Tries + 1;
      end;
    end;
    RegDeleteKeyIncludingSubkeys(HKCU, OldUninstKey);   // drop a stale key if the uninstaller didn't
  end;
end;

// ---- Autostart (registry is the single source of truth; mirrors Services\System\StartupManager.cs) ----
// The Run value says WHAT to launch; a StartupApproved record (byte 0 = 0x02 enabled / 0x03 disabled) is the
// on/off flag Windows honors at login. StartupApproved is kept across uninstall so a reinstall restores the
// user's last choice; only the Run value is removed (see the [Registry] line above).

// 0 = no StartupApproved record, 2 = enabled, 3 = disabled.
function ApprovedState: Integer;
var
  data: AnsiString;
begin
  Result := 0;
  if RegQueryBinaryValue(HKCU, ApprovedKey, StartupValueName, data) and (Length(data) >= 1) then
    if (Ord(data[1]) and 1) = 1 then Result := 3 else Result := 2;
end;

// Tasks-page checkbox default = the current registry state (2 -> on, 3 -> off; no record -> on only if a Run
// value already exists, i.e. a pre-Model-B user who had autostart enabled without a StartupApproved marker).
function AutostartDefaultChecked: Boolean;
begin
  case ApprovedState of
    3: Result := False;
    2: Result := True;
  else
    Result := RegValueExists(HKCU, RunKey, StartupValueName);
  end;
end;

// Write the 12-byte StartupApproved record. Byte 0 is the state; the timestamp is left zero (Windows only
// reads byte 0 to decide — the app writes a real timestamp, the installer doesn't need to).
procedure WriteApproved(State: Integer);
var
  blob: AnsiString;
begin
  blob := #0#0#0#0#0#0#0#0#0#0#0#0;
  blob[1] := Chr(State);
  RegWriteBinaryValue(HKCU, ApprovedKey, StartupValueName, blob);
end;

procedure ApplyAutostart;
var
  hadRun, hadApproved: Boolean;
begin
  hadRun := RegValueExists(HKCU, RunKey, StartupValueName);
  hadApproved := RegValueExists(HKCU, ApprovedKey, StartupValueName);

  // Always keep a Run value present (the app is always listed in Windows' Startup). Never overwrite an
  // existing one — that would wipe a user's --hidden choice.
  if not hadRun then
    RegWriteStringValue(HKCU, RunKey, StartupValueName, '"' + ExpandConstant('{app}\{#MyAppExeName}') + '"');

  if WizardSilent then
  begin
    // Auto-update: never disturb an established on/off state — only seed the record when it's missing.
    if not hadApproved then
    begin
      if hadRun then WriteApproved(2)   // legacy user who had autostart on (Run value, no marker) -> keep on
      else WriteApproved(3);            // fresh silent install -> default off
    end;
  end
  else
    // Interactive install: honor the tasks-page checkbox.
    if WizardIsTaskSelected('autostart') then WriteApproved(2) else WriteApproved(3);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  i: Integer;
begin
  if (CurPageID = wpSelectTasks) and (not gTasksInit) then
  begin
    gTasksInit := True;
    for i := 0 to WizardForm.TasksList.Items.Count - 1 do
      if Pos('sign in', WizardForm.TasksList.Items[i]) > 0 then
        WizardForm.TasksList.Checked[i] := AutostartDefaultChecked;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ApplyAutostart;
end;
