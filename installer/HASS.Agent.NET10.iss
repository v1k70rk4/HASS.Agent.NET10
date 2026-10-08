#ifndef MyAppVersion
#define MyAppVersion "10.9.1-beta.3"
#endif

; The file version resource takes numbers only: a pre-release ("10.9.0-beta.1") keeps its
; suffix everywhere else, and loses it here.
#if Pos("-", MyAppVersion) > 0
  #define MyAppNumericVersion Copy(MyAppVersion, 1, Pos("-", MyAppVersion) - 1)
#else
  #define MyAppNumericVersion MyAppVersion
#endif

#define MyAppName "HASS.Agent .NET10"
#define MyAppExeName "HASS.Agent.NET10.exe"
#define MyAppPublisher "v1k70rk4"

[Setup]
AppId={{8E71E6C1-B215-4C54-B8A5-A7172D7CF3D2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoTextVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=HASS.Agent.NET10-Setup-{#MyAppVersion}
SetupIconFile=..\src\HASS.Agent.NET10\Assets\hassagent.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
; Signed builds come from build-exe.ps1 -Tag, which defines SignSetup and hands
; over the "release" sign tool command on the ISCC command line. Without it
; (CI, plain local builds) the installer is produced unsigned.
#ifdef SignSetup
SignTool=release
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"

[CustomMessages]
english.TaskAutostart=Start automatically on login
english.TaskGroupStartup=Startup:
english.TaskInstallService=Install and start the optional system service
english.TaskGroupOptional=Optional components:
english.TaskCleanInstall=Clean install (remove existing settings, API key, and log files)
english.TaskGroupAdvanced=Advanced:
english.StatusFirewall=Configuring firewall...
english.TaskGroupAccess=Who will use HASS.Agent on this PC? (its settings hold the Home Assistant token)
english.TaskAccessMe=Only me
english.TaskAccessAll=Every user of this PC

hungarian.TaskAutostart=Automatikus indítás bejelentkezéskor
hungarian.TaskGroupStartup=Indítás:
hungarian.TaskInstallService=Opcionális rendszerszolgáltatás telepítése és indítása
hungarian.TaskGroupOptional=Opcionális összetevők:
hungarian.TaskCleanInstall=Tiszta telepítés (meglévő beállítások, API kulcs és naplófájlok törlése)
hungarian.TaskGroupAdvanced=Haladó:
hungarian.StatusFirewall=Tűzfal beállítása...
hungarian.TaskGroupAccess=Ki fogja használni a HASS.Agentet ezen a gépen? (a beállításai a Home Assistant tokent is tartalmazzák)
hungarian.TaskAccessMe=Csak én
hungarian.TaskAccessAll=A gép minden felhasználója

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:TaskGroupStartup}"
Name: "installservice"; Description: "{cm:TaskInstallService}"; GroupDescription: "{cm:TaskGroupOptional}"; Flags: unchecked
; checkedonce: Setup remembers task selections across runs (UsePreviousTasks),
; so one past clean install would silently repeat on every later update.
; This keeps the task available but never pre-ticked on an upgrade.
Name: "cleaninstall"; Description: "{cm:TaskCleanInstall}"; GroupDescription: "{cm:TaskGroupAdvanced}"; Flags: unchecked checkedonce
Name: "accessme"; Description: "{cm:TaskAccessMe}"; GroupDescription: "{cm:TaskGroupAccess}"; Flags: exclusive; Check: IsFirstInstall
Name: "accessall"; Description: "{cm:TaskAccessAll}"; GroupDescription: "{cm:TaskGroupAccess}"; Flags: exclusive unchecked; Check: IsFirstInstall

[Files]
Source: "..\artifacts\HASS.Agent.NET10\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\HASS.Agent.NET10"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HASS.Agent.NET10"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} Local API"""; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StatusFirewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#MyAppName} Local API"" dir=in action=allow protocol=TCP localport=5115 profile=private program=""{app}\{#MyAppExeName}"" enable=yes"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StatusFirewall}"

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--stop-service --quiet"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-service --quiet"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-notifications"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName} Local API"""; Flags: runhidden waituntilterminated

[UninstallDelete]
; Written by the app as administrator (what the Windows service may run), not by Setup.
Type: files; Name: "{app}\service-policy.json"

[Code]
var
  ExistingServiceInstalled: Boolean;
  TrayWasRunning: Boolean;
  PreviousVersion: string;

{ The version this setup replaces, from its own uninstall entry. The agent reports a
  finished update to Home Assistant ("updated from X to Y"); when nobody is logged in the
  service does that, and it cannot know X on its own if the version it replaces predates
  its bookkeeping. The installer always knows, whatever was installed before. }
function ReadPreviousVersion(): string;
begin
  Result := '';
  RegQueryStringValue(
    HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E71E6C1-B215-4C54-B8A5-A7172D7CF3D2}_is1',
    'DisplayVersion',
    Result);
end;

{ No HASS.Agent .NET10 installed yet: the question about who uses it is asked. }
function IsFirstInstall(): Boolean;
begin
  Result := ReadPreviousVersion() = '';
end;

{ The user logged on to the PC who started Setup. Setup itself may run as another account,
  an administrator whose password was typed in; that account is added too, below. }
function OriginalUserName(): string;
var
  NameFile: string;
  Lines: TArrayOfString;
  ResultCode: Integer;
begin
  Result := '';
  NameFile := ExpandConstant('{tmp}\hass-agent-user.txt');
  if ExecAsOriginalUser(ExpandConstant('{cmd}'), '/C echo %USERNAME%>"' + NameFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and LoadStringsFromFile(NameFile, Lines) and (GetArrayLength(Lines) > 0) then
    Result := Trim(Lines[0]);
end;

function RunHidden(FileName: string; Parameters: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ True when the setup was started by the HASS.Agent silent self-update.
  In that case the installer runs as SYSTEM (session 0), so launching the
  tray app here would make it invisible — a watchdog in the user session
  restarts it instead. }
function IsSilentUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/SILENTUPDATE') = 0 then
    begin
      Result := True;
      exit;
    end;
end;

function IsServiceInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'query "HASS.Agent.NET10.Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

function IsTrayRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\cmd.exe'),
    '/C tasklist /FI "IMAGENAME eq {#MyAppExeName}" /NH | find /I "{#MyAppExeName}" >NUL',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Result := ResultCode = 0;
end;

{ Who may use the shared settings. A first install asks (only the user who installs, or
  every user, as every version before 10.9.1 had it); an update, including a silent one from
  Home Assistant that runs as SYSTEM, keeps what the PC has. The app does the work. }
procedure EnsureConfigDirectoryPermissions();
var
  Users: string;
  Original: string;
begin
  { PreviousVersion, read before the files went in: by now this setup is registered itself.
    "Only me" needs a person who chose it: a silent install (winget, a deployment tool, an
    install as SYSTEM) keeps the folder open to every user, as before; the app asks later. }
  if (PreviousVersion = '') and WizardIsTaskSelected('accessme') and (not WizardSilent())
    and (CompareText(ExpandConstant('{username}'), 'SYSTEM') <> 0)
    and (Copy(ExpandConstant('{username}'), Length(ExpandConstant('{username}')), 1) <> '$') then
  begin
    Users := '"' + ExpandConstant('{username}') + '"';
    Original := OriginalUserName();
    if (Original <> '') and (CompareText(Original, ExpandConstant('{username}')) <> 0) then
      Users := Users + ' "' + Original + '"';
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access only ' + Users + ' --quiet');
  end
  else if PreviousVersion = '' then
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access everyone --quiet')
  else
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access keep --quiet');
end;

procedure StopInstalledService();
begin
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop "HASS.Agent.NET10.Service"');
end;

procedure StopRunningTrayApp();
begin
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--exit --quiet');
    Sleep(2000);
    if IsTrayRunning() then
    begin
      RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM "{#MyAppExeName}" /T /F');
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    PreviousVersion := ReadPreviousVersion();
    ExistingServiceInstalled := IsServiceInstalled();
    TrayWasRunning := IsTrayRunning();
    StopRunningTrayApp();
    if ExistingServiceInstalled then
    begin
      StopInstalledService();
      Sleep(1500);
    end;

    { Destructive, so never in silent mode: unattended updates (from Home
      Assistant or in-app) must not wipe settings under any circumstances. }
    if WizardIsTaskSelected('cleaninstall') and (not WizardSilent()) then
    begin
      DelTree(ExpandConstant('{commonappdata}\HASS.Agent.NET10'), True, True, True);
      { Also remove legacy directories so the migration does not restore old settings. }
      DelTree(ExpandConstant('{commonappdata}\HASS.Agent.Companion'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\HASS.Agent.Companion'), True, True, True);
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    EnsureConfigDirectoryPermissions();

    { Before the service starts: it reads this file on start and removes it once the
      update is reported. }
    if (PreviousVersion <> '') and (PreviousVersion <> '{#MyAppVersion}') then
      SaveStringToFile(ExpandConstant('{commonappdata}\HASS.Agent.NET10\updated-from'), PreviousVersion, False);

    { Installing the service also approves what the current settings ask it to run
      (service-policy.json next to the program): an update keeps everything working. }
    if ExistingServiceInstalled or WizardIsTaskSelected('installservice') then
    begin
      RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--install-service --quiet');
    end;

    if not IsSilentUpdate() then
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;
