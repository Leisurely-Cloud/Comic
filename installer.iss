; Comic Downloader Inno Setup 安装脚本
; 下载 Inno Setup: https://jrsoftware.org/isinfo.php

#define MyAppName "Comic Downloader"
#define MyAppNameCN "漫画下载器"
#ifndef MyAppVersion
#define MyAppVersion "2.4.0"
#endif
#define MyAppPublisher "Leisurely Cloud"
#define MyAppURL "https://github.com/Leisurely-Cloud/Comic"
#define MyAppExeName "Comic.WinUI.exe"
#define DotNetRuntimeMajor "9"
#define WindowsAppRuntimeName "Microsoft.WindowsAppRuntime.1.8"
#define WindowsAppRuntimeMinVersion "8000.616.304.0"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE
OutputDir=installer-output
OutputBaseFilename=ComicDownloader-{#MyAppVersion}-Setup
SetupIconFile=app\frontend-winui\src\Comic.WinUI\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "tools\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\frontend\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; 文件关联（可选）
Root: HKA; Subkey: "Software\Classes\.cbz\OpenWithProgids"; ValueType: string; ValueName: "ComicDownloader.cbz"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\ComicDownloader.cbz"; ValueType: string; ValueName: ""; ValueData: "CBZ 漫画文件"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ComicDownloader.cbz\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\ComicDownloader.cbz\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Code]
const
  DotNetRuntimeDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/9.0/runtime';
  WindowsAppRuntimeDownloadUrl = 'https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads-archive#windows-app-sdk-18';

function HasDotNetRuntime(): Boolean;
var
  FindRec: TFindRec;
  RuntimeRoot: String;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{pf64}\dotnet\shared\Microsoft.NETCore.App');
  if FindFirst(AddBackslash(RuntimeRoot) + '{#DotNetRuntimeMajor}.*', FindRec) then
  begin
    try
      repeat
        if DirExists(AddBackslash(RuntimeRoot) + FindRec.Name) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function HasWindowsAppRuntime(): Boolean;
var
  ResultCode: Integer;
  PowerShellPath: String;
  Parameters: String;
begin
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$runtime = Get-AppxPackage -Name ''{#WindowsAppRuntimeName}'' -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Architecture -eq ''X64'' -and $_.Status -eq ''Ok'' -and ' +
    '$_.Version -ge [Version]''{#WindowsAppRuntimeMinVersion}'' }; ' +
    'if ($runtime) { exit 0 } else { exit 1 }"';
  Result := Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

procedure OpenDownloadPage(const Url: String);
var
  ErrorCode: Integer;
begin
  ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function InitializeSetup(): Boolean;
var
  MissingDotNet: Boolean;
  MissingWindowsAppRuntime: Boolean;
  MessageText: String;
begin
  MissingDotNet := not HasDotNetRuntime();
  MissingWindowsAppRuntime := not HasWindowsAppRuntime();
  Result := not MissingDotNet and not MissingWindowsAppRuntime;
  if Result then
    Exit;

  MessageText := '安装漫画下载器前，需要先安装以下系统运行时：' + #13#10 + #13#10;
  if MissingDotNet then
    MessageText := MessageText + '- .NET {#DotNetRuntimeMajor} x64 Runtime' + #13#10;
  if MissingWindowsAppRuntime then
    MessageText := MessageText + '- Windows App Runtime 1.8 x64（至少 {#WindowsAppRuntimeMinVersion}）' + #13#10;
  MessageText := MessageText + #13#10 + '这些运行时不会包含在本安装包中。是否打开微软官方下载页面？';

  if SuppressibleMsgBox(MessageText, mbCriticalError, MB_YESNO, IDNO) = IDYES then
  begin
    if MissingDotNet then
      OpenDownloadPage(DotNetRuntimeDownloadUrl);
    if MissingWindowsAppRuntime then
      OpenDownloadPage(WindowsAppRuntimeDownloadUrl);
  end;
end;
