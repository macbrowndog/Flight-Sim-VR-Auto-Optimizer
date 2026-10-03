#ifndef AppVersion
  #define AppVersion "2.4.2"
#endif
#ifndef SourceDir
  #define SourceDir "..\outputs\publish-2.4.2"
#endif
#ifndef OutputDir
  #define OutputDir "..\outputs\release"
#endif

[Setup]
AppId={{6D47A75E-08A2-4A41-9423-3AD9D1149438}
AppName=VR Auto-Optimizer
AppVersion={#AppVersion}
AppVerName=VR Auto-Optimizer {#AppVersion}
AppPublisher=Andrew Brown
AppPublisherURL=https://flightdeck-tools.andrewmacbrown.chatgpt.site/
AppSupportURL=https://github.com/macbrowndog/Flight-Sim-VR-Auto-Optimizer/issues
AppUpdatesURL=https://github.com/macbrowndog/Flight-Sim-VR-Auto-Optimizer/releases/latest
DefaultDirName={autopf}\FlightDeck Tools\VR Auto-Optimizer
DefaultGroupName=FlightDeck Tools
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=VR-Auto-Optimizer-{#AppVersion}-Setup
SetupIconFile=..\SimVROptimizer.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\SimVROptimizer.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Andrew Brown
VersionInfoDescription=VR Auto-Optimizer installer
VersionInfoProductName=VR Auto-Optimizer
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{autoprograms}\FlightDeck Tools\VR Auto-Optimizer"; Filename: "{app}\SimVROptimizer.exe"
Name: "{autodesktop}\VR Auto-Optimizer"; Filename: "{app}\SimVROptimizer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SimVROptimizer.exe"; Description: "Launch VR Auto-Optimizer"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\SimVROptimizer.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Remove saved profiles, logs, performance history and recovery data as well?',
      mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{localappdata}\SimVROptimizer'), True, True, True);
  end;
end;
