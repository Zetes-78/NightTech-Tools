; Night Tech Wipe installer (Inno Setup 6)
; Built by build.ps1 or the GitHub workflow:  ISCC /DAppVersion=0.1.0 installer\NightTechWipe.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Night Tech Wipe"
#define AppPublisher "Night Tech Solutions"
#define AppURL "https://nighttech.solutions"
#define AppExe "NightTechWipe.exe"

[Setup]
AppId={{BB3DED9E-A42E-4261-BDCA-879ED50F9812}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={localappdata}\Programs\Night Tech Wipe
DefaultGroupName=Night Tech Wipe
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=NightTechWipe-Setup
SetupIconFile=NightTechWipe.ico
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
Source: "..\publish\wipe\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Night Tech Wipe"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Night Tech Wipe"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Night Tech Wipe"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights (needed to erase whole drives), so launch it through the shell.
Filename: "{app}\{#AppExe}"; Description: "Start Night Tech Wipe now"; Flags: shellexec nowait postinstall skipifsilent
