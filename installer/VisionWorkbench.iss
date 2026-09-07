#define MyAppName "VisionWorkbench"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "VisionWorkbench"
#define MyAppExeName "VisionWorkbench.exe"

[Setup]
AppId={{B3D8F3C1-2AF5-4E6E-8B7A-1F1E1EA0D001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\VisionWorkbench
DefaultGroupName=VisionWorkbench
OutputDir=..\artifacts\installer
OutputBaseFilename=VisionWorkbench-{#MyAppVersion}-win-x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
Uninstallable=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\artifacts\commercial\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\VisionWorkbench"; Permissions: users-modify
Name: "{commonappdata}\VisionWorkbench\backups"; Permissions: users-modify
Name: "{localappdata}\VisionWorkbench"; Permissions: users-modify

[Icons]
Name: "{group}\VisionWorkbench"; Filename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\VisionWorkbench"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch VisionWorkbench"; Flags: nowait postinstall skipifsilent
