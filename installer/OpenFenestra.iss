#ifndef AppVersion
  #error Build with scripts/build-installer.ps1
#endif

[Setup]
AppId={{E750B846-31E7-4FCF-926B-C40901F019B8}
AppName=OpenFenestra
AppVersion={#AppVersion}
AppPublisher=Alan-R-Lee and contributors
AppPublisherURL=https://github.com/Alan-R-Lee/OpenFenestra
AppSupportURL=https://github.com/Alan-R-Lee/OpenFenestra/issues
AppUpdatesURL=https://github.com/Alan-R-Lee/OpenFenestra/releases
DefaultDirName={localappdata}\Programs\OpenFenestra
DefaultGroupName=OpenFenestra
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=OpenFenestra-{#AppVersion}-Setup-{#Variant}-x64
SetupIconFile=..\assets\OpenFenestra.ico
UninstallDisplayIcon={app}\OpenFenestra.exe
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
LZMADictionarySize=32768
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.DesktopIcon=创建桌面快捷方式
chinesesimp.Startup=登录 Windows 后自动运行（分栏默认折叠）
chinesesimp.RuntimeTitle=安装 .NET 桌面运行时
chinesesimp.RuntimeInfo=轻量版需要 .NET 10 x64 桌面运行时。请点击下方按钮打开 Microsoft 官方下载页，安装桌面运行时后返回本向导继续。如果不希望另行安装运行时，请使用 Offline 安装包。
chinesesimp.RuntimeDownload=打开 Microsoft 官方下载页
chinesesimp.RuntimeMissing=尚未检测到 .NET 10 x64 桌面运行时。安装完成后请重试，或改用 Offline 安装包。
english.DesktopIcon=Create a desktop shortcut
english.Startup=Start at Windows logon (groups start collapsed)
english.RuntimeTitle=Install the .NET Desktop Runtime
english.RuntimeInfo=The Light edition requires the .NET 10 x64 Desktop Runtime. Open the Microsoft download page below, install the Desktop Runtime, then return here to continue. Use the Offline setup to avoid a separate runtime installation.
english.RuntimeDownload=Open the Microsoft download page
english.RuntimeMissing=The .NET 10 x64 Desktop Runtime was not detected. Install it and retry, or use the Offline setup.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "startup"; Description: "{cm:Startup}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\OpenFenestra.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\FenestraShellBridge.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\CONTRIBUTING.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OpenFenestra"; Filename: "{app}\OpenFenestra.exe"
Name: "{userdesktop}\OpenFenestra"; Filename: "{app}\OpenFenestra.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenFenestra.exe"; Parameters: "--enable-startup --tray"; Tasks: startup; Flags: runhidden nowait
Filename: "{app}\OpenFenestra.exe"; Description: "OpenFenestra"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\OpenFenestra.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopOpenFenestra"

[Code]
var
  RuntimePage: TWizardPage;

function RuntimeAtRoot(const Root: String): Boolean;
var
  Names: TFindRec;
begin
  Result := False;
  if (Root = '') or not FileExists(AddBackslash(Root) + 'dotnet.exe') then Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.0.*', Names) then
  try
    repeat
      if (Pos('-', Names.Name) = 0) and FileExists(AddBackslash(Root) +
        'shared\Microsoft.WindowsDesktop.App\' + Names.Name + '\PresentationFramework.dll') then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(Names);
  finally
    FindClose(Names);
  end;
end;

function HasDesktopRuntime: Boolean;
var
  Root: String;
begin
  Result := RuntimeAtRoot(GetEnv('DOTNET_ROOT_X64')) or RuntimeAtRoot(GetEnv('DOTNET_ROOT'));
  if not Result then
    if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
      Result := RuntimeAtRoot(Root);
  if not Result then Result := RuntimeAtRoot(ExpandConstant('{pf64}\dotnet'));
end;

procedure OpenRuntimeDownload(Sender: TObject);
var
  Code: Integer;
begin
  ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure InitializeWizard;
var
  Info: TNewStaticText;
  Download: TNewButton;
begin
  RuntimePage := CreateCustomPage(wpWelcome, CustomMessage('RuntimeTitle'), CustomMessage('RuntimeTitle'));
  Info := TNewStaticText.Create(RuntimePage);
  Info.Parent := RuntimePage.Surface;
  Info.Caption := CustomMessage('RuntimeInfo');
  Info.WordWrap := True;
  Info.Width := RuntimePage.SurfaceWidth;
  Info.Height := ScaleY(110);
  Download := TNewButton.Create(RuntimePage);
  Download.Parent := RuntimePage.Surface;
  Download.Top := ScaleY(125);
  Download.Width := ScaleX(260);
  Download.Caption := CustomMessage('RuntimeDownload');
  Download.OnClick := @OpenRuntimeDownload;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = RuntimePage.ID then
#if Variant == "Light"
    Result := HasDesktopRuntime;
#else
    Result := True;
#endif
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
#if Variant == "Light"
  if (CurPageID = RuntimePage.ID) and not HasDesktopRuntime then
  begin
    MsgBox(CustomMessage('RuntimeMissing'), mbInformation, MB_OK);
    Result := False;
  end;
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
#if Variant == "Light"
  if not HasDesktopRuntime then
  begin
    Result := CustomMessage('RuntimeMissing');
    Exit;
  end;
#endif
  if FileExists(ExpandConstant('{app}\OpenFenestra.exe')) then
    Exec(ExpandConstant('{app}\OpenFenestra.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
