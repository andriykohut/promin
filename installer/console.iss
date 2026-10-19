; Promin-Console-Setup.exe: installs the console and opens its discovery port in Windows Firewall.
; build.ps1 publishes the apps into ..\artifacts\publish and passes AppVersion.

[Setup]
AppId={{559A23A1-EEB1-4D0A-95E6-B15DCB4E947F}
AppName=Promin
AppVersion={#AppVersion}
DefaultDirName={autopf}\Promin\Console
DisableDirPage=yes
DisableProgramGroupPage=yes
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
; Transparent renders of ..\assets\brand\promin-lockup-light.svg, centred in the Finished page's side panel, same sizes.
WizardImageFile=images\lockup-202x386.png,images\lockup-269x515.png,images\lockup-336x643.png,images\lockup-403x772.png,images\lockup-430x824.png
OutputBaseFilename=Promin-Console-Setup
SetupIconFile=..\src\Promin.Master\Assets\AppIcon.ico
UninstallDisplayName=Promin
UninstallDisplayIcon={app}\Promin.Master.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Messages]
english.WinVersionTooLowError=Promin needs Windows 11. This PC runs an older version of Windows.
ukrainian.WinVersionTooLowError=Promin потребує Windows 11. На цьому ПК стоїть старіша версія Windows.

[InstallDelete]
; Removes the previous version's program files on upgrade. The console's data is in %LocalAppData%\Promin, never in {app}.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "..\artifacts\publish\console\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Promin"; Filename: "{app}\Promin.Master.exe"

[Run]
; The console hears agents announce themselves on UDP 17844. netsh adds a duplicate rule for each add, so a reinstall
; deletes the old one first; the delete fails harmlessly when there is none.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Promin console"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Promin console"" dir=in action=allow protocol=UDP localport=17844 program=""{app}\Promin.Master.exe"""; Flags: runhidden
; The console keeps its data in the signed-in user's %LocalAppData%, so it must not inherit setup's elevation.
Filename: "{app}\Promin.Master.exe"; Description: "{cm:LaunchProgram,Promin}"; Flags: postinstall skipifsilent nowait runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Promin console"""; Flags: runhidden; RunOnceId: "DeleteConsoleFirewallRule"
