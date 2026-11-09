; ============================================================================
;  日程桌面（open-calendar-desktop）Windows 安装包脚本
;
;  本地编译（需要 Inno Setup 6.3+），产物统一落在 bin 下：
;    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\open-calendar.iss
;  或指定参数：
;    ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\src\CalendarApp\bin\Release\publish\win-x64 /DOutputDir=..\src\CalendarApp\bin\Release\installer open-calendar.iss
;
;  CI 编译：.github/workflows/release.yml（推送 v* 标签时自动执行）
; ============================================================================

; ---------- 可由命令行 /D 覆盖的变量 ----------
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\CalendarApp\bin\Release\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\src\CalendarApp\bin\Release\installer"
#endif

; ---------- 固定信息 ----------
#define AppName "日程桌面"
#define AppExeName "CalendarApp.exe"
#define AppPublisher "MrMu666"
#define AppUrl "https://github.com/MrMu666/open-calendar"
#define AppIdValue "{{A3F1C6D2-7B4E-4C89-9F1A-2E5D8B7C4A61}"

[Setup]
AppId={#AppIdValue}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoCopyright=Copyright (C) {#AppPublisher}

; 默认安装到 Program Files（勾选“仅为我安装”时自动改用 %LocalAppData%\Programs）
DefaultDirName={autopf}\OpenCalendar
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; 需要管理员权限，但允许用户选择按用户安装（无需 UAC）
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 仅支持 64 位 Windows 10 1809+（Inno Setup 6.3+ 语法）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; 输出
OutputDir={#OutputDir}
OutputBaseFilename=open-calendar-{#AppVersion}-win-x64-setup
SetupIconFile=setup-icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; 压缩与外观
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
SetupLogging=yes
AllowNoIcons=yes

; 安装/升级时检测应用是否在运行（与 App.xaml.cs 中的单实例互斥体同名）
AppMutex=Global\CalendarApp_SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
; 中文语言包随 Inno Setup 6.5+ 官方提供；CI 探测到就传入 /DHasChinese，未探测到则只用英文
#ifdef HasChinese
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 自包含发布目录：主程序 + 运行时 + 依赖
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\卸载 {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
{ 卸载时清理应用自己写入的开机启动项：
  HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CalendarApp
  仅在值指向本安装目录时才删除，避免误删用户手动配置的其他路径。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunKey: string;
  Value: string;
  ExePath: string;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  ExePath := ExpandConstant('{app}\{#AppExeName}');

  if RegQueryStringValue(HKCU, RunKey, 'CalendarApp', Value) then
    if (CompareText(Value, ExePath) = 0) or
       (CompareText(Value, '"' + ExePath + '"') = 0) then
      RegDeleteValue(HKCU, RunKey, 'CalendarApp');
end;
