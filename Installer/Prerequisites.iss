function DotNetAvailable: Boolean;
var Release: Cardinal;
begin
#ifdef InstallerTestBuild
  if GetEnv('MCPANEL_SETUP_TEST_DOTNET') = 'missing' then begin
    Result := FileExists(GetEnv('MCPANEL_SETUP_TEST_STATE') + '\dotnet.done');
    exit;
  end;
#endif
  Result := (RegQueryDWordValue(HKLM32, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) or
    RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)) and
    (Release >= {#MinimumNetRelease});
end;

function VisualCppAvailable: Boolean;
var MS, LS: Cardinal; PreviousRedirection: Boolean;
begin
#ifdef InstallerTestBuild
  if GetEnv('MCPANEL_SETUP_TEST_VC') = 'missing' then begin
    Result := FileExists(GetEnv('MCPANEL_SETUP_TEST_STATE') + '\vc.done');
    exit;
  end;
#endif
  PreviousRedirection := EnableFsRedirection(False);
  try
    Result := GetVersionNumbers(ExpandConstant('{sys}\MSVCR100.dll'), MS, LS);
    if Result then Result := (MS > {#MinimumVcMS}) or ((MS = {#MinimumVcMS}) and (LS >= {#MinimumVcLS}));
    if Result then begin
      Result := GetVersionNumbers(ExpandConstant('{sys}\MSVCP100.dll'), MS, LS);
      if Result then Result := (MS > {#MinimumVcMS}) or ((MS = {#MinimumVcMS}) and (LS >= {#MinimumVcLS}));
    end;
  finally EnableFsRedirection(PreviousRedirection); end;
end;

function DependencyText(Available: Boolean): String;
begin
  if Available then Result := '已满足要求'
  else Result := '安装时自动补齐';
end;

procedure RunPrerequisite(const Name, FileName, Arguments: String; IsDotNet: Boolean);
var ExitCode: Integer; Progress: TOutputProgressWizardPage; Available: Boolean;
begin
  Progress := CreateOutputProgressPage('正在安装必备运行库', '请稍候，正在准备 MCPanel 的运行环境。');
  Progress.SetText('正在安装 ' + Name, '安装程序不会自动重启电脑，请等待此步骤完成。');
  Progress.Show;
  try
    Progress.SetProgress(0, 100);
    if not Exec(ExpandConstant('{tmp}\') + FileName, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('无法启动 ' + Name + ' 安装程序：' + SysErrorMessage(ExitCode));
    Log('Prerequisite ' + Name + ' returned ' + IntToStr(ExitCode));
    if (ExitCode = 3010) or (ExitCode = 1641) then RuntimeRestartRequired := True
    else begin
      if (ExitCode <> 0) and (ExitCode <> 1638) then
        RaiseException(Name + ' 安装失败（代码 ' + IntToStr(ExitCode) + '）。主程序未修改，请解决运行库安装问题后重试。');
      if IsDotNet then Available := DotNetAvailable else Available := VisualCppAvailable;
      if not Available then RaiseException(Name + ' 安装后仍未满足要求。主程序未修改，请重启 Windows 后重试。');
    end;
    Progress.SetProgress(100, 100);
  finally Progress.Hide; end;
end;

procedure InstallPrerequisites;
begin
  if not DotNetAvailable then RunPrerequisite('.NET Framework 4.8', 'dotnet48.exe', '{#DotNetArguments}', True);
  if not VisualCppAvailable then RunPrerequisite('VC++ 2010 SP1 x64', 'vcredist2010-x64.exe', '{#VcArguments}', False);
end;
