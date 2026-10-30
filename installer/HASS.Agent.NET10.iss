#ifndef MyAppVersion
#define MyAppVersion "10.11.0-beta.3"
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
english.UninstallSettings=Remove the settings too?%n%nThey hold the Home Assistant token, the MQTT password and the API key. Keep them only if you will install HASS.Agent .NET10 again.
english.AccessUserUnknown=Setup could not find out which user started it, so HASS.Agent .NET10 was limited to %1 only.%n%nIf that is not you, an administrator can add you in HASS.Agent: Danger Zone, Users of this PC.
english.AccessFailed=HASS.Agent .NET10 could not be limited to the chosen users; every user of this PC can use it for now.%n%nIt can be set in HASS.Agent: Danger Zone, Users of this PC.
english.KioskTaskNotDisabled=The kiosk mode task (HASS.Agent.NET10\Kiosk) could not be paused, so the files cannot be replaced safely. Nothing was changed. Try again, or turn kiosk mode off first.
english.KioskTaskNotEnabled=The kiosk mode task (HASS.Agent.NET10\Kiosk) could not be turned back on. Until it is, the app does not come back for the kiosk users when it is closed.%n%nTurn it on in Task Scheduler, or save kiosk mode again in HASS.Agent: Danger Zone, Kiosk mode.

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
hungarian.UninstallSettings=A beállításokat is törlöd?%n%nBennük van a Home Assistant token, az MQTT jelszó és az API kulcs. Csak akkor tartsd meg őket, ha újra telepíted a HASS.Agent .NET10-et.
hungarian.AccessUserUnknown=A telepítő nem tudta megállapítani, melyik felhasználó indította, ezért a HASS.Agent .NET10 csak %1 számára lett korlátozva.%n%nHa ez nem te vagy, egy rendszergazda felvehet a HASS.Agentben: Danger Zone, A gép felhasználói.
hungarian.AccessFailed=A HASS.Agent .NET10-et nem sikerült a választott felhasználókra korlátozni; egyelőre a gép minden felhasználója használhatja.%n%nA HASS.Agentben beállítható: Danger Zone, A gép felhasználói.
hungarian.KioskTaskNotDisabled=A kioszk mód feladatát (HASS.Agent.NET10\Kiosk) nem sikerült szüneteltetni, ezért a fájlokat nem lehet biztonságosan lecserélni. Nem változott semmi. Próbáld újra, vagy előbb kapcsold ki a kioszk módot.
hungarian.KioskTaskNotEnabled=A kioszk mód feladatát (HASS.Agent.NET10\Kiosk) nem sikerült visszakapcsolni. Amíg nincs bekapcsolva, a kioszk-felhasználóknál bezárás után nem jön vissza az alkalmazás.%n%nKapcsold be a Feladatütemezőben, vagy mentsd el újra a kioszk módot a HASS.Agentben: Danger Zone, Kioszk mód.

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
; Removes the kiosk task and group, and gives the kiosk users their write access back.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--kiosk off --quiet"; Flags: runhidden waituntilterminated skipifdoesntexist
; The task goes either way: an off that failed keeps it, and it would run a program that is gone.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""HASS.Agent.NET10\Kiosk"" /F"; Flags: runhidden waituntilterminated
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
  KioskTaskDisabled: Boolean;

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

{ Secur32's GetUserNameEx: the account of this process's own token, as DOMAIN\name
  (NameSamCompatible = 2), in Unicode. }
function GetUserNameExW(NameFormat: Integer; NameBuffer: String; var Size: DWORD): Boolean;
  external 'GetUserNameExW@secur32.dll stdcall';

{ The account Setup runs as, as DOMAIN\name: the user, or an administrator whose password
  was typed in. From Setup's own token, so the domain and the name are the same account's
  (the environment may still be the user's after an elevation with other credentials), and
  an accent in the name survives; whoami's redirected output would be in the OEM code page. }
function SetupUserName(): string;
var
  Buffer: String;
  Size: DWORD;
begin
  Size := 512;
  SetLength(Buffer, Size);
  if GetUserNameExW(2, Buffer, Size) and (Size > 0) then
    Result := Copy(Buffer, 1, Size)
  else
    Result := ExpandConstant('{username}');
end;

{ The user logged on to the PC who started Setup, as DOMAIN\name. Written by that user into
  ProgramData, where every user may create a file (Setup's own temp folder is the elevated
  account's, where another user cannot write, which is exactly the case this is for), under a
  name made up for this run, so nobody can put a file of that name there first; read only when
  the user's own write succeeded. PowerShell writes it in the ANSI code page, which is what
  LoadStringsFromFile reads, so an accent in the name survives. }
function OriginalUserName(): string;
var
  NameFile: string;
  Lines: TArrayOfString;
  ResultCode: Integer;
begin
  Result := '';
  NameFile := ExpandConstant('{commonappdata}\hass-agent-setup-user-')
    + GetDateTimeString('yyyymmddhhnnsszzz', #0, #0) + '-' + IntToStr(Random(2147483647)) + '.txt';
  DeleteFile(NameFile);
  if ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "[IO.File]::WriteAllText(''' + NameFile + ''', $env:USERDOMAIN + ''\'' + $env:USERNAME, '
      + '[Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage))"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0) and LoadStringsFromFile(NameFile, Lines) and (GetArrayLength(Lines) > 0) then
    Result := Trim(Lines[0]);
  DeleteFile(NameFile);
end;

function RunHidden(FileName: string; Parameters: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
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
  Elevated: string;
  Original: string;
begin
  { PreviousVersion, read before the files went in: by now this setup is registered itself.
    "Only me" needs a person who chose it: a silent install (winget, a deployment tool, an
    install as SYSTEM) keeps the folder open to every user, as before; the app asks later. }
  if (PreviousVersion = '') and WizardIsTaskSelected('accessme') and (not WizardSilent())
    and (CompareText(ExpandConstant('{username}'), 'SYSTEM') <> 0)
    and (Copy(ExpandConstant('{username}'), Length(ExpandConstant('{username}')), 1) <> '$') then
  begin
    Elevated := SetupUserName();
    Users := '"' + Elevated + '"';
    Original := OriginalUserName();
    if (Original <> '') and (CompareText(Original, Elevated) <> 0) then
      Users := Users + ' "' + Original + '"';
    if not RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access only ' + Users + ' --quiet') then
    begin
      Log('Limiting the settings to ' + Users + ' failed.');
      MsgBox(CustomMessage('AccessFailed'), mbError, MB_OK);
    end
    else if Original = '' then
    begin
      { "Only me" with nobody known to be me but the account Setup runs as: said, not left to be found out. }
      Log('The user who started Setup could not be determined; limited to ' + Elevated + '.');
      MsgBox(FmtMessage(CustomMessage('AccessUserUnknown'), [Elevated]), mbInformation, MB_OK);
    end;
  end
  else if (PreviousVersion = '') and WizardIsTaskSelected('accessall') and (not WizardSilent()) then
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access everyone --quiet')
  else if PreviousVersion = '' then
    { Nobody chose: open as before, without recording a choice, so the app asks when its
      window is opened on a PC with a user who is not an administrator. }
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access open --quiet')
  else
    RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--settings-access keep --quiet');
end;

const
  KioskTask = 'HASS.Agent.NET10\Kiosk';

{ Text as UTF-16 little endian, the way Task Scheduler keeps its task files. }
function Utf16(Text: AnsiString): AnsiString;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(Text) do
    Result := Result + Text[I] + #0;
end;

{ The kiosk task: 0 not there, 1 on, 2 turned off, -1 there but its state not known. Both
  from its file under System32\Tasks, which Task Scheduler keeps for every task: no
  PowerShell, and the same in every language of Windows. A task turned off has
  <Enabled>false</Enabled> in its settings; one that is on has true there, or nothing. }
function KioskTaskState(): Integer;
var
  TaskFile: string;
  Xml: AnsiString;
  Settings: AnsiString;
  First, Last: Integer;
begin
  TaskFile := ExpandConstant('{sys}\Tasks\') + KioskTask;
  if not FileExists(TaskFile) then
  begin
    Result := 0;
    exit;
  end;

  Result := -1;
  if not LoadStringFromFile(TaskFile, Xml) then
    exit;
  First := Pos(Utf16('<Settings>'), Xml);
  Last := Pos(Utf16('</Settings>'), Xml);
  if (First = 0) or (Last <= First) then
    exit;
  Settings := Copy(Xml, First, Last - First);
  if Pos(Utf16('<Enabled>false</Enabled>'), Settings) > 0 then
    Result := 2
  else
    Result := 1;
end;

{ /DISABLE or /ENABLE on the kiosk task, tried three times a second apart. }
function ChangeKioskTask(Switch: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to 3 do
  begin
    if RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Change /TN "' + KioskTask + '" ' + Switch) then
    begin
      Result := True;
      exit;
    end;
    Sleep(1000);
  end;
end;

{ In kiosk mode a closed tray app is started again at once: not while the files are replaced,
  or the old program would be running again. With no task, or one an administrator turned
  off, there is nothing to do, and it is left as it is. A task that is on and cannot be
  paused, or whose file cannot be read, stops Setup before anything is changed.
  DeinitializeSetup turns back on only what was paused here, also when the install failed. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  case KioskTaskState() of
    1:
      begin
        KioskTaskDisabled := ChangeKioskTask('/DISABLE');
        if not KioskTaskDisabled then
        begin
          Log('The kiosk task could not be paused; Setup stops before changing anything.');
          Result := CustomMessage('KioskTaskNotDisabled');
        end;
      end;
    -1:
      begin
        Log('The kiosk task''s file could not be read; Setup stops before changing anything.');
        Result := CustomMessage('KioskTaskNotDisabled');
      end;
  end;
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

    { Installing the service keeps what an administrator approved for it before
      (service-policy.json next to the program), so an update keeps everything working.
      Anything new in the settings waits for the approval prompt in the app. }
    if ExistingServiceInstalled or WizardIsTaskSelected('installservice') then
    begin
      RunHidden(ExpandConstant('{app}\{#MyAppExeName}'), '--install-service --quiet');
    end;

    { As the user who started the setup, not with its administrator rights: the app runs
      the commands in the settings, which other users may be able to write. }
    if not IsSilentUpdate() then
      ExecAsOriginalUser(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure DeinitializeSetup();
begin
  { Whatever became of the install, the kiosk users get their app back from the task; when
    the task cannot be turned on again, that is said, not left to be found out. Once on, it
    is run right away: Setup ended the kiosk users' apps, which leaves nothing in the event
    log for the task to start them on, and the kiosk users would wait for its next minute. }
  if KioskTaskDisabled then
  begin
    if not ChangeKioskTask('/ENABLE') then
    begin
      Log('The kiosk task could not be turned back on.');
      if not WizardSilent() then
        MsgBox(CustomMessage('KioskTaskNotEnabled'), mbError, MB_OK);
    end
    else if not RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Run /TN "' + KioskTask + '"') then
      Log('The kiosk task could not be run now; it starts the kiosk users'' apps within a minute.');
  end;
end;

{ The settings stay after an uninstall unless the person asks otherwise: a reinstall finds
  them again. Asked, not assumed, since they hold the tokens; never in a silent uninstall. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent()) then
  begin
    if MsgBox(CustomMessage('UninstallSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{commonappdata}\HASS.Agent.NET10'), True, True, True);
      { The version that last ran (made by the app, which every user may write) goes with the
        settings: kept with them, a reinstall reports the update from the right version. }
      RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\HASS.Agent.NET10');
    end;
  end;
end;
