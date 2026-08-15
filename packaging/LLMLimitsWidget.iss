#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64-framework-dependent"
#endif

#ifndef InstallerSuffix
  #define InstallerSuffix "framework-dependent"
#endif

#ifndef RequiresRuntime
  #define RequiresRuntime "0"
#endif

#ifndef RuntimeInstallerPath
  #define RuntimeInstallerPath "..\artifacts\runtime\windowsdesktop-runtime-win-x64.exe"
#endif

#define RuntimeInstallerName "windowsdesktop-runtime-win-x64.exe"
#define MyAppName "LLM Limits Widget"
#define MyAppExeName "LLMLimitsWidget.FloatingOverlay.exe"
#define SourceDir PublishDir

[Setup]
AppId={{08A6F418-BE57-4B0C-86E8-42D52A7C2EF7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=LuckyRu
DefaultDirName={autopf}\LLM Limits Widget
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=LLM-Limits-Widget-Setup-{#MyAppVersion}-{#InstallerSuffix}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
#if "1" == RequiresRuntime
Source: "{#RuntimeInstallerPath}"; DestDir: "{tmp}"; Flags: dontcopy
#endif
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--no-ghost"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

#if "1" == RequiresRuntime
[Code]
function HasWindowsDesktopRuntime10(): Boolean;
var
  RuntimeOutputPath: string;
  RuntimeOutput: AnsiString;
  ResultCode: Integer;
begin
  Result := False;
  RuntimeOutputPath := ExpandConstant('{tmp}\llm-limits-dotnet-runtimes.txt');
  DeleteFile(RuntimeOutputPath);

  if Exec(ExpandConstant('{sys}\cmd.exe'), '/C dotnet --list-runtimes > "' + RuntimeOutputPath + '" 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
     LoadStringFromFile(RuntimeOutputPath, RuntimeOutput) then
    Result := Pos('Microsoft.WindowsDesktop.App 10.', RuntimeOutput) > 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  if HasWindowsDesktopRuntime10() then
    exit;

  ExtractTemporaryFile('{#RuntimeInstallerName}');
  if not Exec(ExpandConstant('{tmp}\{#RuntimeInstallerName}'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Не удалось запустить установщик .NET Desktop Runtime.';
    exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Result := 'Установка .NET Desktop Runtime завершилась с кодом ' + IntToStr(ResultCode) + '.';
    exit;
  end;

  NeedsRestart := ResultCode = 3010;
  if not HasWindowsDesktopRuntime10() then
    Result := 'После установки не найден .NET 10 Windows Desktop Runtime.';
end;
#endif
