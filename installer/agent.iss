; Promin Agent Setup.exe: installs the agent as a service and its window on a room PC, asking nothing.
; build.ps1 publishes the apps into ..\artifacts\publish and passes AppVersion.

[Setup]
AppId={{52E1884D-FA31-4B4C-8E89-79701CB1DA67}
AppName=Promin agent
AppVersion={#AppVersion}
DefaultDirName={autopf}\Promin\Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableFinishedPage=yes
; The language follows Windows' display language; Inno Setup would otherwise ask for one.
ShowLanguageDialog=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; QUIC needs Windows 11: .NET's MsQuic takes TLS 1.3 from Schannel.
MinVersion=10.0.22000
WizardStyle=modern
; Transparent renders of ..\assets\brand\promin-beacon-small-light.svg at the image-area sizes for 100% to 200% scaling.
WizardSmallImageFile=images\beacon-58.png,images\beacon-77.png,images\beacon-97.png,images\beacon-116.png,images\beacon-124.png
; PrepareToInstall stops the agent and its window itself; Restart Manager would ask the user to close them.
CloseApplications=no
OutputBaseFilename=Promin Agent Setup
SetupIconFile=..\src\Promin.Master\Assets\AppIcon.ico
UninstallDisplayName=Promin agent
UninstallDisplayIcon={app}\Promin.Agent.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Messages]
english.WinVersionTooLowError=Promin needs Windows 11. This PC runs an older version of Windows.
ukrainian.WinVersionTooLowError=Promin потребує Windows 11. На цьому ПК стоїть старіша версія Windows.

[CustomMessages]
english.InstallFailed=Promin agent was copied but could not be set up as a service (exit code %1). In an administrator terminal in %2, run "Promin.Agent.exe --install" to see why. Then open Promin agent from the Start menu to set the room code.
ukrainian.InstallFailed=Агент Promin скопійовано, але не вдалося налаштувати його як службу (код виходу %1). Щоб побачити причину, виконайте "Promin.Agent.exe --install" у терміналі адміністратора в папці %2. Потім відкрийте Promin agent з меню «Пуск», щоб задати код кімнати.

[InstallDelete]
; Removes the previous version's program files on upgrade. The agent's data is in %ProgramData%\Promin, never in {app}.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "..\artifacts\publish\agent\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\artifacts\publish\agent-window\*"; DestDir: "{app}\Window"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Promin agent"; Filename: "{app}\Window\Promin.Agent.Window.exe"

[UninstallRun]
; Runs before the program files are removed. It keeps the room code, identity and pairing, so a reinstall needs no new pairing.
Filename: "{app}\Promin.Agent.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallAgentService"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{52E1884D-FA31-4B4C-8E89-79701CB1DA67}_is1';

var
  FirstInstall: Boolean;

function InitializeSetup(): Boolean;
begin
  // Read before anything is installed: installing creates the key.
  FirstInstall := not RegKeyExists(HKLM64, UninstallKey);
  Result := True;
end;

// Stops the service (which ends its session process), the window and any leftover agent process, and waits for each.
// Running files cannot be replaced or deleted. Each one may not be running, so results are ignored.
procedure StopAgent();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'stop ProminAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Promin.Agent.Window.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Promin.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// --install leaves an already running service on its old exe, so it is stopped here first. This also covers an agent
// installed by hand, which this setup has no record of.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAgent();
  Result := '';
end;

// usUninstall comes before [UninstallRun] and the file deletion. --uninstall does not wait for the service to stop, and
// a file still in use would make the uninstaller ask for a restart.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopAgent();
end;

// The window is started here rather than from [Run] so that it always runs after --install. It inherits setup's
// elevation, which its requireAdministrator manifest needs. An upgrade keeps the room code, so it opens no window.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  if not Exec(ExpandConstant('{app}\Promin.Agent.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     or (ResultCode <> 0) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('InstallFailed'), [IntToStr(ResultCode), ExpandConstant('{app}')]),
      mbError, MB_OK, IDOK)
  else if FirstInstall then
    Exec(ExpandConstant('{app}\Window\Promin.Agent.Window.exe'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
