#ifndef GeneratedDirectory
  #error Build with Build-OnlineInstaller.ps1.
#endif
#include GeneratedDirectory + "\Header.iss"
#if DecodeVer(VER) != ExpectedCompilerVersion
  #error Build with the pinned Inno Setup compiler from Installer/dependencies.json.
#endif

[Setup]
#ifdef InstallerTestBuild
AppId=MCPanel.Setup.IntegrationTest
#else
AppId={{4877D128-D808-4B6B-B4CB-D4D0476F5275}
#endif
AppName=MCPanel
AppVersion={#AppVersion}
AppPublisher=MCPanel
AppPublisherURL=https://github.com/vguangshen/MCPanel
AppSupportURL=https://github.com/vguangshen/MCPanel/issues
AppUpdatesURL=https://github.com/vguangshen/MCPanel/releases
DefaultDirName={sd}\MCPanel
DefaultGroupName=MCPanel
UninstallDisplayName=MCPanel
UninstallDisplayIcon={app}\MCPanel.exe
UninstallFilesDir={app}\Maintenance
SetupIconFile=..\Assets\AppIcon\mcpanel-logo.ico
OutputBaseFilename=MCPanel-Setup
VersionInfoVersion={#AppVersion}.0
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=6.1sp1
PrivilegesRequired=admin
WizardStyle=modern
WizardSizePercent=120
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableDirPage=no
AllowNoIcons=yes
UsePreviousAppDir=yes
SetupMutex=MCPanel.Setup.v1
AppMutex=Local\MCPanel.MainWindow.v1
CloseApplications=no
RestartApplications=no
ArchiveExtraction=full
Compression=lzma2
SolidCompression=yes
ExtraDiskSpaceRequired={#RequiredSpace}
ChangesAssociations=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "installer.marker"; DestDir: "{app}\Maintenance"; Flags: ignoreversion uninsneveruninstall

[Icons]
Name: "{group}\MCPanel"; Filename: "{app}\MCPanel.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MCPanel"; Filename: "{app}\MCPanel.exe"; WorkingDir: "{app}"; Check: WantDesktopShortcut

[Run]
Filename: "{app}\MCPanel.exe"; Description: "启动 MCPanel"; Flags: postinstall nowait skipifsilent; Check: CanLaunchPanel

[Messages]
SelectDirLabel3=选择 MCPanel 的安装目录，然后点击“安装”。已有安装会保留原配置和业务数据。
SelectDirDesc=选择安装位置
SelectDirBrowseLabel=安装目录：
ButtonInstall=安装(&I)
FinishedHeadingLabel=MCPanel 安装完成
FinishedLabelNoIcons=MCPanel 已安装到所选目录。数据库、站点和已有配置均予以保留。
FinishedLabel=MCPanel 已安装到所选目录。数据库、站点和已有配置均予以保留。
ConfirmUninstall=确定卸载 MCPanel 面板程序吗？%n%n数据库、站点、运行组件和配置将保留。

[Code]
var
  ProgramFiles, PreservedNames, PreservedKeys: TStringList;
  DownloadPage: TDownloadWizardPage;
  DependencyStatus: TNewStaticText;
  DesktopShortcut: TNewCheckBox;
  RuntimeRestartRequired, ProgramDeployed: Boolean;
  PayloadRoot, TransactionRoot: String;
  UpdateLockHandle: LongWord;
#ifdef InstallerTestBuild
  TestFailureInjected: Boolean;
#endif

function CreateFileW(FileName: String; Access, ShareMode, Security, Creation, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function ReplaceFileW(Destination, Source: String; Backup, Flags, Exclude, Reserved: LongWord): Boolean;
  external 'ReplaceFileW@kernel32.dll stdcall';

#include GeneratedDirectory + "\Layout.iss"
#include "Deployment.iss"
#include "Prerequisites.iss"

function WantDesktopShortcut: Boolean;
begin
  Result := DesktopShortcut.Checked;
end;

function CanLaunchPanel: Boolean;
begin
  Result := not RuntimeRestartRequired;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeRestartRequired;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (TransactionRoot <> '') then
    if not DelTree(TransactionRoot, True, True, True) then Log('Installer transaction cleanup will be retried manually: ' + TransactionRoot);
end;

procedure InitializeWizard;
var Note: TNewStaticText;
begin
  ProgramFiles := TStringList.Create;
  PreservedNames := TStringList.Create;
  PreservedKeys := TStringList.Create;
  LoadDeploymentLists;
  DownloadPage := CreateDownloadPage('正在下载安装', '正在获取经过校验的程序和必备运行库，请保持网络连接。', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  WizardForm.DiskSpaceLabel.Visible := False;
  DependencyStatus := TNewStaticText.Create(WizardForm);
  DependencyStatus.Parent := WizardForm.SelectDirPage;
  DependencyStatus.Left := WizardForm.DirEdit.Left;
  DependencyStatus.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(24);
  DependencyStatus.Width := WizardForm.SelectDirPage.Width - DependencyStatus.Left;
  DependencyStatus.Height := ScaleY(74);
  DependencyStatus.AutoSize := False;
  DependencyStatus.Caption := '安装前自动检查' + #13#10 + #13#10 +
    '.NET Framework：' + DependencyText(DotNetAvailable) + #13#10 +
    'VC++ 2010 SP1 x64：' + DependencyText(VisualCppAvailable);
  DesktopShortcut := TNewCheckBox.Create(WizardForm);
  DesktopShortcut.Parent := WizardForm.SelectDirPage;
  DesktopShortcut.Left := DependencyStatus.Left;
  DesktopShortcut.Top := DependencyStatus.Top + DependencyStatus.Height + ScaleY(12);
  DesktopShortcut.Width := DependencyStatus.Width;
  DesktopShortcut.Height := ScaleY(22);
  DesktopShortcut.Caption := '创建桌面快捷方式';
  DesktopShortcut.Checked := True;
  Note := TNewStaticText.Create(WizardForm);
  Note.Parent := WizardForm.SelectDirPage;
  Note.Left := DependencyStatus.Left;
  Note.Top := DesktopShortcut.Top + DesktopShortcut.Height + ScaleY(12);
  Note.Width := DependencyStatus.Width;
  Note.Height := ScaleY(34);
  Note.AutoSize := False;
  Note.WordWrap := True;
  Note.Caption := '缺少的运行库会从微软官方下载并安装。已有数据库、站点和运行组件将保留。';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then WizardForm.NextButton.Caption := '安装(&I)';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then begin
    try ValidateInstallDirectory(ExpandConstant('{app}'));
    except
      SuppressibleMsgBox(GetExceptionMessage, mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var PackagePath, ActualVersion, Phase, DownloadError: String;
begin
  Result := '';
  if ProgramDeployed then exit;
  Phase := '检查安装目录';
  try
    ValidateInstallDirectory(ExpandConstant('{app}'));
    if CheckForMutexes('Local\MCPanel.MainWindow.v1') then
      RaiseException('请从托盘菜单完全退出 MCPanel，再重新点击安装。');
    Phase := '下载安装文件';
    DownloadPage.Clear;
    PackagePath := ExpandConstant('{param:PACKAGEFILE|}');
    if PackagePath = '' then begin
      PackagePath := ExpandConstant('{tmp}\MCPanel.zip');
      DownloadPage.Add('{#PackageUrl}', 'MCPanel.zip', '{#PackageHash}');
    end else if CompareText(GetSHA256OfFile(PackagePath), '{#PackageHash}') <> 0 then
      RaiseException('本地安装包校验失败，原程序未修改。请使用与安装器版本一致的官方 ZIP。');
    if not DotNetAvailable then DownloadPage.Add('{#DotNetUrl}', 'dotnet48.exe', '{#DotNetHash}');
    if not VisualCppAvailable then DownloadPage.Add('{#VcUrl}', 'vcredist2010-x64.exe', '{#VcHash}');
    DownloadError := '';
    DownloadPage.Show;
    try
      try DownloadPage.Download;
      except DownloadError := GetExceptionMessage; end;
    finally DownloadPage.Hide; end;
    if DownloadError <> '' then RaiseException(DownloadError);
    Phase := '解压并校验主程序';
    PayloadRoot := ExpandConstant('{tmp}\Payload');
    ExtractArchive(PackagePath, PayloadRoot, '', True, nil);
    if not GetVersionNumbersString(PayloadRoot + '\MCPanel.exe', ActualVersion) or
      (ActualVersion <> '{#AppVersion}.0') then RaiseException('安装包版本不匹配，原程序未修改。');
    Log('Package executable version validated: ' + ActualVersion);
    Phase := '读取并合并原配置';
    AcquireUpdateLock(ExpandConstant('{app}'));
    MergeOriginalConfiguration(ExpandConstant('{app}'), PayloadRoot + '\MCPanel.exe.config');
    Phase := '检查程序文件占用';
    CheckChangedProgramFiles(ExpandConstant('{app}'), PayloadRoot);
    Phase := '安装必备运行库';
    InstallPrerequisites;
    Phase := '安装主程序';
    DeployProgram(ExpandConstant('{app}'), PayloadRoot);
    ProgramDeployed := True;
  except
    Result := Phase + '：' + GetExceptionMessage;
    Log('MCPanel installation stopped: ' + Result);
  end;
  ReleaseUpdateLock;
end;

procedure DeinitializeSetup;
begin
  ReleaseUpdateLock;
end;

function InitializeUninstall: Boolean;
begin
  ProgramFiles := TStringList.Create;
  PreservedNames := TStringList.Create;
  PreservedKeys := TStringList.Create;
  LoadDeploymentLists;
  Result := False;
  try
    if CheckForMutexes('Local\MCPanel.MainWindow.v1') then
      RaiseException('请先从托盘菜单完全退出 MCPanel，再卸载面板程序。');
    CheckHostedServiceRegistration;
    CheckUninstallFiles;
    Result := True;
  except SuppressibleMsgBox(GetExceptionMessage, mbError, MB_OK, IDOK); end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveOwnedProgramFiles;
end;
