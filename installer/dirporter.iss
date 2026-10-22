; DirPorter 安装包脚本（Inno Setup 6+）
;
; 用法：
;   1) 安装 Inno Setup 6：https://jrsoftware.org/isdl.php
;   2) 先发布：  .\publish.ps1
;   3) 编译安装包：
;        & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
;            /DMyVersion=0.2.0 `
;            /DSourceExe="C:\...\dist\0.2.0\dirporter.exe" `
;            installer\dirporter.iss
;   4) 产物：installer\output\DirPorter-<版本>-setup.exe
;
; 说明：安装包与"免安装单文件 exe"两种分发方式并存 ——
;   安装包会创建开始菜单项、卸载项，并把程序放进 Program Files（需要管理员）。
;   内置更新功能对两种方式都有效：它替换的就是 dirporter.exe 本身。

#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif

#ifndef SourceExe
  #define SourceExe "..\dist\0.2.0\dirporter.exe"
#endif

#ifndef OutputDir
  #define OutputDir "output"
#endif

[Setup]
AppId={{8D3A2C64-7E1B-4E4F-9C1D-7A5B2E9F4C31}
AppName=DirPorter
AppVersion={#MyVersion}
AppVerName=DirPorter {#MyVersion}
AppPublisher=DirPorter
DefaultDirName={autopf}\DirPorter
DefaultGroupName=DirPorter
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=DirPorter-{#MyVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 单文件 exe 自身带清单；安装包安装到 Program Files 需要管理员
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\dirporter.exe
SetupIconFile=..\src\DirPorter.App\Resources\app.ico
; 若已签名，可在编译前用 tools\sign.ps1 对 setup.exe 再签一次（或在此配置 SignTool）

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "dirporter.exe"; Flags: ignoreversion
; 可选：若同目录有 latest.json（内网分发），一并安装
Source: "..\dist\{#MyVersion}\latest.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\DirPorter"; Filename: "{app}\dirporter.exe"
Name: "{group}\卸载 DirPorter"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DirPorter"; Filename: "{app}\dirporter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\dirporter.exe"; Description: "启动 DirPorter"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时不动用户数据（%ProgramData%\DirPorter 里的台账/日志），需要彻底清理请手动删除。
Type: filesandordirs; Name: "{app}\*.log"
