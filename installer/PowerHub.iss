#ifndef AppVersion
  #define AppVersion "1.0.3"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "1.0.3"
#endif

[Setup]
AppId={{F8421AA2-85D8-4E06-B227-BDBD43B58BC9}
AppName=电栖 · Perivolt
AppVersion={#AppVersion}
AppVerName=电栖 · Perivolt {#AppVersion}
AppPublisher=Perivolt contributors
AppComments=Windows 外设电量中心
VersionInfoVersion={#AppFileVersion}
VersionInfoDescription=电栖 · Perivolt 安装程序
DefaultDirName={code:DefaultInstallDirectory}
DisableDirPage=no
DefaultGroupName=电栖
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
UninstallDisplayName=电栖 · Perivolt
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
Name: "startup"; Description: "登录 Windows 后自动启动电栖"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "..\dist\PowerHub\*"; DestDir: "{app}"; Excludes: "*.pdb,artifacts,artifacts\*,settings.json,settings.*.json,battery-*.jsonl,plugins\character-themes,plugins\character-themes\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\电栖"; Filename: "{app}\PowerHub.exe"; Parameters: "--show"; WorkingDir: "{app}"; Check: not WizardNoIcons
Name: "{userdesktop}\电栖"; Filename: "{app}\PowerHub.exe"; Parameters: "--show"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PowerHub.exe"; Parameters: "--show"; WorkingDir: "{app}"; Description: "启动电栖"; Flags: nowait postinstall skipifsilent

[InstallDelete]
Type: files; Name: "{userprograms}\PowerHub.lnk"
Type: files; Name: "{userdesktop}\PowerHub.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PowerHub"; ValueData: """{app}\PowerHub.exe"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Code]
function GetDriveType(RootPath: string): Integer;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function RecommendedDirectory: string;
var
  Letter: Integer;
  Root: string;
begin
  Result := '';
  for Letter := Ord('D') to Ord('Z') do
  begin
    Root := Chr(Letter) + ':\';
    if (CompareText(Root, AddBackslash(ExpandConstant('{sd}'))) <> 0) and
       (GetDriveType(Root) = 3) and DirExists(Root) then
    begin
      Result := Root + 'Apps\PowerHub';
      Exit;
    end;
  end;
end;

function DefaultInstallDirectory(Param: string): string;
begin
  Result := RecommendedDirectory;
end;

function HasExplicitDirectory: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(Copy(ParamStr(Index), 1, 5), '/DIR=') = 0 then
      Result := True;
end;

procedure InitializeWizard;
var
  PreviousDrive: string;
begin
  if not HasExplicitDirectory then
  begin
    PreviousDrive := ExtractFileDrive(WizardForm.DirEdit.Text);
    if (CompareText(PreviousDrive, 'C:') = 0) or
       (CompareText(PreviousDrive, ExpandConstant('{sd}')) = 0) then
      WizardForm.DirEdit.Text := RecommendedDirectory;
  end;
  WizardForm.SelectDirLabel.Caption := '请选择 PowerHub 的安装位置。优先推荐非系统盘，安装前可自由更改。';
  if WizardForm.DirEdit.Text = '' then
    WizardForm.SelectDirLabel.Caption := '未找到可用的非系统固定磁盘，请手动选择安装目录后继续。';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and (Trim(WizardForm.DirEdit.Text) = '') then
  begin
    Result := False;
    if WizardSilent then Log('No non-system drive is available. Specify /DIR explicitly.')
    else MsgBox('请选择安装目录后继续。PowerHub 不会自动安装到 C 盘。', mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    { Also remove a startup entry enabled later from PowerHub's settings. User settings are preserved. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerHub');
end;
