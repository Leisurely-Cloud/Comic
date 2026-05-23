; Comic Downloader Inno Setup 安装脚本
; 下载 Inno Setup: https://jrsoftware.org/isinfo.php

#define MyAppName "Comic Downloader"
#define MyAppNameCN "漫画下载器"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Leisurely Cloud"
#define MyAppURL "https://github.com/Leisurely-Cloud/Comic"
#define MyAppExeName "Comic.WinUI.exe"

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
; SetupIconFile=app\frontend-winui\src\Comic.WinUI\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "tools\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 前端文件
Source: "publish\frontend\*"; DestDir: "{app}\frontend"; Flags: ignoreversion recursesubdirs createallsubdirs
; 后端文件
Source: "publish\backend\*"; DestDir: "{app}\backend"; Flags: ignoreversion recursesubdirs createallsubdirs
; Python 运行时
Source: "publish\python\*"; DestDir: "{app}\python"; Flags: ignoreversion recursesubdirs createallsubdirs
; 启动脚本
Source: "publish\ComicDownloader.bat"; DestDir: "{app}"; Flags: ignoreversion
; 初始化脚本
Source: "publish\setup.bat"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\frontend\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\frontend\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\frontend\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// 检查 .NET 依赖（自包含版本不需要）
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

[Registry]
; 文件关联（可选）
Root: HKA; Subkey: "Software\Classes\.cbz\OpenWithProgids"; ValueType: string; ValueName: "ComicDownloader.cbz"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\ComicDownloader.cbz"; ValueType: string; ValueName: ""; ValueData: "CBZ 漫画文件"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ComicDownloader.cbz\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\frontend\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\ComicDownloader.cbz\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\frontend\{#MyAppExeName}"" ""%1"""
