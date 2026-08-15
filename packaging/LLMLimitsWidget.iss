#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "LLM Limits Widget"
#define MyAppExeName "LLMLimitsWidget.FloatingOverlay.exe"
#define SourceDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{08A6F418-BE57-4B0C-86E8-42D52A7C2EF7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=LuckyRu
DefaultDirName={autopf}\LLM Limits Widget
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=LLM-Limits-Widget-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--no-ghost"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent
