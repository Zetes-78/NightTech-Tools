; Night Tech Recovery installer (Inno Setup 6)
; Built by build.ps1 or the GitHub workflow:  ISCC /DAppVersion=0.1.0 installer\NightTechRecovery.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Night Tech Recovery"
#define AppPublisher "Night Tech Solutions"
#define AppURL "https://nighttech.solutions"
#define AppExe "NightTechRecovery.exe"

[Setup]
AppId={{6F1E2B7C-4A3D-4C8E-9B21-7D5E0C3A9F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={localappdata}\Programs\Night Tech Recovery
DefaultGroupName=Night Tech Recovery
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=NightTechRecovery-Setup
SetupIconFile=NightTechRecovery.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\recovery\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Night Tech Recovery"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Night Tech Recovery"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Night Tech Recovery"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights (needed to read whole drives), so launch it through the shell.
Filename: "{app}\{#AppExe}"; Description: "Start Night Tech Recovery now"; Flags: shellexec nowait postinstall skipifsilent
