; Night Tech Burn-in installer (Inno Setup 6)
; Built by build.ps1 or the GitHub workflow:  ISCC /DAppVersion=0.1.0 installer\NightTechBurnIn.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Night Tech Burn-in"
#define AppPublisher "Night Tech Solutions"
#define AppURL "https://nighttech.solutions"
#define AppExe "NightTechBurnIn.exe"

[Setup]
AppId={{99DB9926-F539-4988-8C52-C4FC0CB971D2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={localappdata}\Programs\Night Tech Burn-in
DefaultGroupName=Night Tech Burn-in
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=NightTechBurnIn-Setup
SetupIconFile=NightTechBurnIn.ico
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
Source: "..\publish\burnin\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Night Tech Burn-in"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Night Tech Burn-in"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Night Tech Burn-in"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights (needed to erase whole drives), so launch it through the shell.
Filename: "{app}\{#AppExe}"; Description: "Start Night Tech Burn-in now"; Flags: shellexec nowait postinstall skipifsilent
