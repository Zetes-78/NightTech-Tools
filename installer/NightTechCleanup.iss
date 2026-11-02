; Night Tech Cleanup installer (Inno Setup 6)
; Built by build.ps1 or the GitHub workflow:  ISCC /DAppVersion=0.1.0 installer\NightTechCleanup.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Night Tech Cleanup"
#define AppPublisher "Night Tech Solutions"
#define AppURL "https://nighttech.solutions"
#define AppExe "NightTechCleanup.exe"

[Setup]
AppId={{5F3F5A6A-FAFF-48A8-B012-B144F13E54D7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={localappdata}\Programs\Night Tech Cleanup
DefaultGroupName=Night Tech Cleanup
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=NightTechCleanup-Setup
SetupIconFile=NightTechCleanup.ico
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
Source: "..\publish\cleanup\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Night Tech Cleanup"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Night Tech Cleanup"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Night Tech Cleanup"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights (needed to erase whole drives), so launch it through the shell.
Filename: "{app}\{#AppExe}"; Description: "Start Night Tech Cleanup now"; Flags: shellexec nowait postinstall skipifsilent
