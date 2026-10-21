#ifndef AppVersion
  #define AppVersion "1.0.5"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "1.0.5"
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
UninstallDisplayIcon={app}\Perivolt.exe
UninstallDisplayName=电栖 · Perivolt
; Allow Restart Manager to close the app before replacing its files.
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=Perivolt-Setup-{#AppVersion}-x64
ShowLanguageDialog=no
UsePreviousTasks=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked
Name: "startup"; Description: "登录 Windows 后自动启动电栖"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "..\dist\PowerHub\*"; DestDir: "{app}"; Excludes: "PowerHub.exe,*.pdb,artifacts,artifacts\*,settings.json,settings.*.json,battery-*.jsonl,plugins\character-themes,plugins\character-themes\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\电栖"; Filename: "{app}\Perivolt.exe"; Parameters: "{code:PostInstallParameters}"; WorkingDir: "{app}"; Check: not WizardNoIcons
Name: "{userdesktop}\电栖"; Filename: "{app}\Perivolt.exe"; Parameters: "{code:PostInstallParameters}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Perivolt.exe"; Parameters: "{code:PostInstallParameters}"; WorkingDir: "{app}"; Description: "启动电栖"; Flags: nowait postinstall skipifsilent

[InstallDelete]
Type: files; Name: "{app}\PowerHub.exe"
Type: files; Name: "{userprograms}\PowerHub.lnk"
Type: files; Name: "{userdesktop}\PowerHub.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PowerHub"; ValueData: """{app}\Perivolt.exe"" {code:PostInstallParameters|startup}"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PowerHub"; Tasks: not startup; Flags: deletevalue

[Code]
var
  StartupTaskInitialized: Boolean;

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
      Result := Root + 'Apps\Perivolt';
      Exit;
    end;
  end;
end;

function DefaultInstallDirectory(Param: string): string;
begin
  Result := RecommendedDirectory;
end;

function QuoteCommandLineArgument(Value: string): string;
var
  Index, Slashes: Integer;
begin
  Result := '"';
  Slashes := 0;
  for Index := 1 to Length(Value) do
  begin
    if Value[Index] = '\' then
      Slashes := Slashes + 1
    else
    begin
      if Value[Index] = '"' then
        Result := Result + StringOfChar('\', Slashes * 2 + 1)
      else
        Result := Result + StringOfChar('\', Slashes);
      Result := Result + Value[Index];
      Slashes := 0;
    end;
  end;
  Result := Result + StringOfChar('\', Slashes * 2) + '"';
end;

function PostInstallParameters(Param: string): string;
var
  DataDirectory: string;
begin
  if Param = 'startup' then Result := '--startup'
  else Result := '--show';
  DataDirectory := ExpandConstant('{param:PERIVOLT_DATA_DIR|}');
  if DataDirectory <> '' then
  begin
    DataDirectory := RemoveBackslashUnlessRoot(ExpandFileName(DataDirectory));
    Result := Result + ' --data-dir ' + QuoteCommandLineArgument(DataDirectory);
  end;
end;

function HasExplicitTaskSelection: Boolean;
var
  Index: Integer;
  Parameter: string;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    Parameter := Uppercase(ParamStr(Index));
    if (Pos('/TASKS=', Parameter) = 1) or
       (Pos('/MERGETASKS=', Parameter) = 1) or
       (Pos('/LOADINF=', Parameter) = 1) then
      Result := True;
  end;
end;

procedure InitializeWizard;
begin
  WizardForm.SelectDirLabel.Caption := '请选择 Perivolt 的安装位置。优先推荐非系统盘，安装前可自由更改。';
  if WizardForm.DirEdit.Text = '' then
    WizardForm.SelectDirLabel.Caption := '未找到可用的非系统固定磁盘，请手动选择安装目录后继续。';
end;

procedure CurPageChanged(CurPageID: Integer);
var
  StartupCommand: string;
begin
  if (CurPageID = wpSelectTasks) and not StartupTaskInitialized then
  begin
    StartupTaskInitialized := True;
    if not HasExplicitTaskSelection then
    begin
      { Reflect startup changes made in the app after the previous installation. }
      if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
         'PowerHub', StartupCommand) and (Trim(StartupCommand) <> '') then
        WizardSelectTasks('startup')
      else
        WizardSelectTasks('!startup');
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and (Trim(WizardForm.DirEdit.Text) = '') then
  begin
    Result := False;
    if WizardSilent then Log('No non-system drive is available. Specify /DIR explicitly.')
    else MsgBox('请选择安装目录后继续。Perivolt 不会自动安装到 C 盘。', mbInformation, MB_OK);
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('Local\PowerHub.TrayApp');
  if not Result then
  begin
    Log('Perivolt is running; uninstall cancelled before removing files.');
    if not UninstallSilent then
      MsgBox('电栖仍在运行，请先从托盘退出后再卸载。', mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    { Also remove a startup entry enabled later from the app's settings. User settings are preserved. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerHub');
end;
