; 游戏双译 安装包（Inno Setup 6）
; 构建：powershell -File installer\build-installer.ps1
; 内容：自带 .NET 运行环境的程序（publish 目录）+ 虚拟声卡安装包（drivers，由软件里的“安装环境”按钮安装）

#define AppName "游戏双译"
#define AppExe "GameVoiceTranslator.exe"
#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif

[Setup]
AppId={{A3870EE6-D118-4D2B-AC0D-A3ABA907E63B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
; 默认装到当前用户目录，不需要管理员权限；装虚拟声卡时软件会单独请求一次授权
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\src\GVT.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=output
OutputBaseFilename={#AppName}-安装包-{#AppVersion}
; 安装 / 卸载时如果软件正在运行，提示先关闭
AppMutex=GVT_GameVoiceTranslator_SingleInstance
CloseApplications=yes

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即打开 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前结束可能还在运行的原声恢复进程
Filename: "{sys}\taskkill.exe"; Parameters: "/IM GVT.Watchdog.exe /F"; Flags: runhidden; RunOnceId: "KillWatchdog"
