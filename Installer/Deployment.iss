function GetFileAttributesW(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function SetFileAttributesW(FileName: String; Attributes: LongWord): Boolean;
  external 'SetFileAttributesW@kernel32.dll stdcall';

function OwnedManifest: String;
begin
  Result := ExpandConstant('{app}\Maintenance\program-files.lst');
end;

function SafeProgramName(const Name: String): Boolean;
var TopLevel: String; Separator: Integer;
begin
  Result := False;
  if (Name = '') or (Name[1] = '\') or (Pos(':', Name) > 0) or (Pos('/', Name) > 0) or
    (Pos('\..\', '\' + Name + '\') > 0) or (Pos('\.\', '\' + Name + '\') > 0) then exit;
  Separator := Pos('\', Name + '\');
  TopLevel := Copy(Name, 1, Separator - 1);
  Result := (PreservedNames.IndexOf(TopLevel) < 0) and (CompareText(Name, 'MCPanel.exe.config') <> 0);
end;

procedure AssertNoLinks(Path: String);
var Attributes: LongWord; Parent: String;
begin
  while Length(Path) > 3 do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then
      RaiseException('安装目录不能通过符号链接或目录联接访问：' + Path);
    Parent := ExtractFileDir(Path);
    if Parent = Path then break;
    Path := Parent;
  end;
end;

procedure ValidateInstallDirectory(const Directory: String);
var Root, WindowsRoot: String; Find: TFindRec; HasEntries: Boolean;
begin
  Root := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
  WindowsRoot := AddBackslash(ExpandConstant('{win}'));
  if (Length(Root) <= 3) or (CompareText(AddBackslash(Root), WindowsRoot) = 0) or
    (CompareText(Copy(AddBackslash(Root), 1, Length(WindowsRoot)), WindowsRoot) = 0) or
    (CompareText(Root, ExpandConstant('{autopf}')) = 0) or
    (Length(Root) + 80 + {#MaximumRelativeLength} > 248) then
      RaiseException('请选择独立的 MCPanel 文件夹，不能使用磁盘根目录、Windows 目录或过长的路径。');
  AssertNoLinks(Root);
  AssertNoLinks(Root + '\Maintenance');
  AssertNoLinks(Root + '\StoreData');
  HasEntries := False;
  if FindFirst(Root + '\*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then HasEntries := True;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
  if HasEntries and not FileExists(Root + '\MCPanel.exe') and not FileExists(Root + '\MCPanel.exe.config') and
    not DirExists(Root + '\StoreData\Updates') and not FileExists(Root + '\Maintenance\installer.marker') then
      RaiseException('该文件夹包含其他文件。请选择空文件夹，或已有的 MCPanel 安装目录。');
end;

function LoadXml(const FileName: String): Variant;
begin
  Result := CreateOleObject('Msxml2.DOMDocument.6.0');
  Result.async := False;
  Result.resolveExternals := False;
  Result.validateOnParse := False;
  Result.setProperty('ProhibitDTD', True);
  if not Result.load(FileName) then RaiseException('配置文件无法读取，原程序未修改：' + FileName);
  if VarIsNull(Result.selectSingleNode('/configuration')) then RaiseException('应用配置缺少 configuration 根节点。');
end;

procedure MergeOriginalConfiguration(const InstallRoot, NewConfig: String);
var Original, Candidate, Latest: String; OldDoc, NewDoc, Nodes, Node, Settings, Target, Matches: Variant;
  I, J: Integer; Key: String; Find: TFindRec;
begin
  Original := InstallRoot + '\MCPanel.exe.config';
  Candidate := InstallRoot + '\StoreData\Updates\Rollback\Current\MCPanel.exe.config';
  if not FileExists(InstallRoot + '\MCPanel.exe') and FileExists(Candidate) then Original := Candidate;
  if not FileExists(Original) then begin
    if FileExists(Candidate) then Original := Candidate
    else begin
      Latest := '';
      if FindFirst(InstallRoot + '\StoreData\Updates\Rollback\Current.failed-*', Find) then begin
        try
          repeat
            Candidate := InstallRoot + '\StoreData\Updates\Rollback\' + Find.Name + '\MCPanel.exe.config';
            if FileExists(Candidate) and (CompareText(Find.Name, Latest) > 0) then begin
              Latest := Find.Name;
              Original := Candidate;
            end;
          until not FindNext(Find);
        finally FindClose(Find); end;
      end;
    end;
  end;
  NewDoc := LoadXml(NewConfig);
  if not FileExists(Original) then begin
    if DirExists(InstallRoot + '\StoreData') then
      RaiseException('已有业务数据，但未找到原应用配置。请保留 StoreData 和回滚副本，恢复原配置后重试。');
    exit;
  end;
  AssertNoLinks(Original);
  OldDoc := LoadXml(Original);
  Nodes := OldDoc.selectNodes('/configuration/appSettings/add');
  Settings := NewDoc.selectSingleNode('/configuration/appSettings');
  if VarIsNull(Settings) then begin
    Settings := NewDoc.createElement('appSettings');
    NewDoc.documentElement.appendChild(Settings);
  end;
  for I := 0 to Nodes.length - 1 do begin
    Node := Nodes.item(I);
    Key := Node.getAttribute('key');
    if (PreservedKeys.IndexOf(Key) < 0) or VarIsNull(Node.getAttribute('value')) then continue;
    Target := Null;
    Matches := Settings.selectNodes('add');
    for J := 0 to Matches.length - 1 do
      if CompareText(Matches.item(J).getAttribute('key'), Key) = 0 then Target := Matches.item(J);
    if VarIsNull(Target) then begin
      Target := NewDoc.createElement('add');
      Target.setAttribute('key', Key);
      Settings.appendChild(Target);
    end;
    Target.setAttribute('value', Node.getAttribute('value'));
  end;
  NewDoc.save(NewConfig);
  Log('Preserved original settings from ' + Original);
end;

procedure AssertFileAvailable(const FileName: String);
var Handle: LongWord;
begin
  if not FileExists(FileName) then exit;
  Handle := CreateFileW(FileName, $80000000, 0, 0, 3, $80, 0);
  if Handle = $FFFFFFFF then RaiseException('文件正在被占用或无法访问，原程序未修改：' + FileName);
  CloseHandle(Handle);
end;

function FileNeedsChange(const Installed, Incoming: String): Boolean;
begin
  Result := not FileExists(Installed);
  if not Result then Result := CompareText(GetSHA256OfFile(Installed), GetSHA256OfFile(Incoming)) <> 0;
end;

procedure CheckChangedProgramFiles(const InstallRoot, Incoming: String);
var I: Integer; Destination, Source: String;
begin
  for I := 0 to ProgramFiles.Count - 1 do begin
    Source := Incoming + '\' + ProgramFiles[I];
    Destination := InstallRoot + '\' + ProgramFiles[I];
    if not FileExists(Source) then RaiseException('安装包文件不完整：' + ProgramFiles[I]);
    AssertNoLinks(Destination);
    if FileNeedsChange(Destination, Source) then AssertFileAvailable(Destination);
  end;
end;

procedure SaveOwnership(const FileName: String);
var Lines: TArrayOfString; I: Integer;
begin
  SetArrayLength(Lines, ProgramFiles.Count);
  for I := 0 to ProgramFiles.Count - 1 do Lines[I] := ProgramFiles[I];
  if not SaveStringsToUTF8File(FileName, Lines, False) then RaiseException('无法保存安装记录：' + FileName);
end;

procedure ReplaceOwnedFile(const Source, Destination: String);
begin
#ifdef InstallerTestBuild
  if not TestFailureInjected and (GetEnv('MCPANEL_SETUP_TEST_FAIL_FILE') <> '') and
    (CompareText(ExtractFileName(Destination), GetEnv('MCPANEL_SETUP_TEST_FAIL_FILE')) = 0) then begin
    TestFailureInjected := True;
    RaiseException('Injected late installer replacement failure.');
  end;
#endif
  if FileExists(Destination) then begin
    if not SetFileAttributesW(Destination, GetFileAttributesW(Destination) and $FFFFFFFE) then
      RaiseException('无法准备替换文件：' + Destination);
    if not ReplaceFileW(Destination, Source, 0, 0, 0, 0) then
      RaiseException('无法原子替换文件：' + Destination + '（' + SysErrorMessage(DLLGetLastError) + '）');
  end else if not RenameFile(Source, Destination) then RaiseException('无法安装文件：' + Destination);
end;

procedure DeployProgram(const InstallRoot, Incoming: String);
var Changed, Completed, Originals: TStringList; I: Integer; Name, ActiveName, Destination, Ready, Backup, Restore, ErrorText: String;
  ManifestExisted, RollbackFailed: Boolean;
begin
  CheckChangedProgramFiles(InstallRoot, Incoming);
  TransactionRoot := InstallRoot + '\Maintenance\Transactions\' + GetDateTimeString('yyyymmddhhnnsszzz', '', '');
  AssertNoLinks(TransactionRoot);
  Changed := TStringList.Create;
  Completed := TStringList.Create;
  Originals := TStringList.Create;
  try
    for I := 0 to ProgramFiles.Count - 1 do begin
      Name := ProgramFiles[I];
      if FileNeedsChange(InstallRoot + '\' + Name, Incoming + '\' + Name) then Changed.Add(Name);
    end;
    if not ForceDirectories(TransactionRoot + '\Original') then RaiseException('无法创建安装目录。请检查目录权限和剩余空间。');
    ManifestExisted := FileExists(OwnedManifest);
    if ManifestExisted and not FileCopy(OwnedManifest, TransactionRoot + '\manifest.previous', True) then
      RaiseException('无法备份原安装记录。原程序未修改。');
    for I := 0 to Changed.Count - 1 do begin
      Name := Changed[I];
      Destination := InstallRoot + '\' + Name;
      Ready := TransactionRoot + '\Ready\' + Name;
      Backup := TransactionRoot + '\Original\' + Name;
      ForceDirectories(ExtractFileDir(Ready));
      if not FileCopy(Incoming + '\' + Name, Ready, True) then RaiseException('无法准备新程序文件，原程序未修改：' + Name);
      SetFileAttributesW(Ready, $80);
      if FileExists(Destination) then begin
        ForceDirectories(ExtractFileDir(Backup));
        if not FileCopy(Destination, Backup, True) then RaiseException('无法备份原程序，原程序未修改：' + Name);
        Originals.Add(Name);
      end;
    end;
    try
      SaveOwnership(OwnedManifest);
      ActiveName := '';
      for I := 0 to Changed.Count - 1 do begin
        Name := Changed[I];
        ActiveName := Name;
        Destination := InstallRoot + '\' + Name;
        ForceDirectories(ExtractFileDir(Destination));
        ReplaceOwnedFile(TransactionRoot + '\Ready\' + Name, Destination);
        Completed.Add(Name);
        ActiveName := '';
      end;
    except
      ErrorText := GetExceptionMessage;
      RollbackFailed := False;
      if (ActiveName <> '') and (Originals.IndexOf(ActiveName) >= 0) then
        if not SetFileAttributesW(InstallRoot + '\' + ActiveName,
          GetFileAttributesW(TransactionRoot + '\Original\' + ActiveName)) then RollbackFailed := True;
      for I := Completed.Count - 1 downto 0 do begin
        Name := Completed[I];
        Destination := InstallRoot + '\' + Name;
        Backup := TransactionRoot + '\Original\' + Name;
        try
          if Originals.IndexOf(Name) >= 0 then begin
            Restore := TransactionRoot + '\Ready\' + Name + '.restore';
            if not FileCopy(Backup, Restore, True) then RaiseException('无法读取回滚文件：' + Name);
            SetFileAttributesW(Restore, $80);
            ReplaceOwnedFile(Restore, Destination);
            SetFileAttributesW(Destination, GetFileAttributesW(Backup));
          end else if not DeleteFile(Destination) then RaiseException('无法撤回新文件：' + Name);
        except
          RollbackFailed := True;
          Log('Rollback: ' + GetExceptionMessage);
        end;
      end;
      if ManifestExisted then FileCopy(TransactionRoot + '\manifest.previous', OwnedManifest, False)
      else DeleteFile(OwnedManifest);
      if RollbackFailed then ErrorText := ErrorText + #13#10 + '部分文件未能自动恢复，请保留：' + TransactionRoot;
      RaiseException(ErrorText);
    end;
    // Keep evidence until Inno finishes shortcuts and registration. Its
    // ssPostInstall event removes only this transaction after full success.
    Log('Program installed. Original program files retained at ' + TransactionRoot);
  finally
    Changed.Free;
    Completed.Free;
    Originals.Free;
  end;
end;

procedure CheckHostedServiceRegistration;
var Names: TArrayOfString; I: Integer; ImagePath, Executable: String;
begin
  Executable := Lowercase(ExpandConstant('{app}\MCPanel.exe'));
  if RegGetSubkeyNames(HKLM, 'SYSTEM\CurrentControlSet\Services', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Names[I], 'ImagePath', ImagePath) then
        if Pos(Executable, Lowercase(ImagePath)) > 0 then
          RaiseException('组件服务仍依赖此面板：' + Names[I] + '。请先在 MCPanel 的服务页处理对应托管服务，再卸载面板。业务数据不会删除。');
end;

procedure LoadOwnedFiles;
var Lines: TArrayOfString; I: Integer;
begin
  if not FileExists(OwnedManifest) then exit;
  if not LoadStringsFromFile(OwnedManifest, Lines) then RaiseException('安装记录无法读取，卸载已停止。');
  ProgramFiles.Clear;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if SafeProgramName(Lines[I]) and (ProgramFiles.IndexOf(Lines[I]) < 0) then ProgramFiles.Add(Lines[I]);
end;

procedure CheckUninstallFiles;
var I: Integer; Destination: String;
begin
  LoadOwnedFiles;
  for I := 0 to ProgramFiles.Count - 1 do
    if SafeProgramName(ProgramFiles[I]) then begin
      Destination := ExpandConstant('{app}\') + ProgramFiles[I];
      AssertNoLinks(Destination);
      AssertFileAvailable(Destination);
    end;
end;

procedure RemoveOwnedProgramFiles;
var I: Integer; Destination, Parent, AppRoot: String;
begin
  AppRoot := ExpandConstant('{app}');
  for I := 0 to ProgramFiles.Count - 1 do
    if SafeProgramName(ProgramFiles[I]) then begin
      Destination := AppRoot + '\' + ProgramFiles[I];
      AssertNoLinks(Destination);
      if FileExists(Destination) then begin
        SetFileAttributesW(Destination, GetFileAttributesW(Destination) and $FFFFFFFE);
        if not DeleteFile(Destination) then RaiseException('无法移除程序文件：' + Destination);
      end;
      Parent := ExtractFileDir(Destination);
      while Length(Parent) > Length(AppRoot) do begin
        RemoveDir(Parent);
        Parent := ExtractFileDir(Parent);
      end;
    end;
  DeleteFile(OwnedManifest);
  DeleteFile(AppRoot + '\Maintenance\installer.marker');
end;
