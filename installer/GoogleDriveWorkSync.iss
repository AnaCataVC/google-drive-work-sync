; Script generated for Inno Setup 6.x
; Application: Google Drive Work Sync
; Architecture: Windows x64 (Unpackaged WinUI 3)

#define MyAppName "Google Drive Work Sync"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "AnaCataVC"
#define MyAppURL "https://github.com/AnaCataVC/google-drive-work-sync"
#define MyAppExeName "GoogleDriveWorkSync.exe"
#define MyAppId "{{8B1A2C3D-4E5F-6A7B-8C9D-0E1F2A3B4C5D}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\GoogleDriveWorkSync
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\releases
OutputBaseFilename=GoogleDriveWorkSync-Setup-v{#MyAppVersion}
SetupIconFile=..\GoogleDriveWorkSync\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Iniciar Google Drive Work Sync automáticamente al iniciar sesión en Windows"; GroupDescription: "Configuración de inicio:"

[Files]
Source: "..\releases\GoogleDriveWorkSync-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Automated startup via registry run key when autostart task is checked
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GoogleDriveWorkSync"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
