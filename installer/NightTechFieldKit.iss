; Night Tech Field Kit installer (Inno Setup 6)
; Built by build.ps1 or the GitHub workflow:  ISCC /DAppVersion=0.1.0 installer\NightTechFieldKit.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Night Tech Field Kit"
#define AppPublisher "Night Tech Solutions"
#define AppURL "https://nighttech.solutions"
#define AppExe "NightTechFieldKit.exe"

[Setup]
AppId={{12D23A3C-076E-444D-8EF7-BAF5E05DC568}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={localappdata}\Programs\Night Tech Field Kit
DefaultGroupName=Night Tech Field Kit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=NightTechFieldKit-Setup
SetupIconFile=NightTechFieldKit.ico
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
Source: "..\publish\fieldkit\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Night Tech Field Kit"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Night Tech Field Kit"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Night Tech Field Kit"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start Night Tech Field Kit now"; Flags: shellexec nowait postinstall skipifsilent
