; Inno Setup script for FineClipboard.
; Version is passed in from CI:  ISCC.exe /DMyAppVersion=0.2.0 FineClipboard.iss
; Installs per-user (no admin / no UAC), matching the app's %LOCALAPPDATA% data store.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "FineClipboard"
#define MyAppExe "FineClipboard.exe"
#define MyAppPublisher "cassiarota"
#define MyAppUrl "https://github.com/cassiarota/fine-clipboard"

[Setup]
AppId={{B6E2F0A7-6C3D-4F1E-9E2A-7A1C4D5E6F70}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=FineClipboard-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startupicon"; Description: "开机自动启动 FineClipboard"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; Everything produced by `dotnet publish` (self-contained single-file exe).
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
; Optional auto-start at login (per-user Run via Startup folder shortcut).
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "立即运行 FineClipboard"; Flags: nowait postinstall skipifsilent
