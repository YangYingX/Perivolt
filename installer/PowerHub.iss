#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{F8421AA2-85D8-4E06-B227-BDBD43B58BC9}
AppName=PowerHub
AppVersion={#AppVersion}
AppVerName=PowerHub {#AppVersion}
AppPublisher=PowerHub
AppComments=Windows 外设电量中心
VersionInfoVersion={#AppVersion}
VersionInfoDescription=PowerHub 安装程序
DefaultDirName={localappdata}\Programs\PowerHub
DefaultGroupName=PowerHub
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern dynamic
WizardSizePercent=110
SetupIconFile=..\Assets\powerhub.ico
UninstallDisplayIcon={app}\PowerHub.exe
UninstallDisplayName=PowerHub
AppMutex=Local\PowerHub.TrayApp
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=PowerHub-Setup-{#AppVersion}-x64
ShowLanguageDialog=no
UsePreviousTasks=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked
Name: "startup"; Description: "登录 Windows 后自动启动 PowerHub"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "..\dist\PowerHub\*"; DestDir: "{app}"; Excludes: "*.pdb,artifacts,artifacts\*,settings.json,settings.*.json,battery-*.jsonl"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\PowerHub"; Filename: "{app}\PowerHub.exe"; WorkingDir: "{app}"; Check: not WizardNoIcons
Name: "{userdesktop}\PowerHub"; Filename: "{app}\PowerHub.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PowerHub"; ValueData: """{app}\PowerHub.exe"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    { Also remove a startup entry enabled later from PowerHub's settings. User settings are preserved. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerHub');
end;
