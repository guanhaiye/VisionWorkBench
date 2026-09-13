#define MyAppName "VisionWorkbench"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "VisionWorkbench"
#define MyAppExeName "VisionWorkbench.exe"

[Setup]
AppId={{B3D8F3C1-2AF5-4E6E-8B7A-1F1E1EA0D001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\VisionWorkbench
DefaultGroupName=VisionWorkbench
OutputBaseFilename=VisionWorkbench-Setup-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
Uninstallable=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\assets\branding\visionworkbench-app.ico
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=VisionWorkbench

[Files]
Source: "..\artifacts\installer-staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\VisionWorkbench"; Permissions: users-modify
Name: "{commonappdata}\VisionWorkbench\Config"; Permissions: users-modify
Name: "{commonappdata}\VisionWorkbench\backups"; Permissions: users-modify
Name: "{localappdata}\VisionWorkbench"; Permissions: users-modify

[Icons]
Name: "{group}\VisionWorkbench"; Filename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\VisionWorkbench"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--environment-check"; Description: "Run environment check"; Flags: waituntilterminated postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "Launch VisionWorkbench"; Flags: nowait postinstall unchecked
