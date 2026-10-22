#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef BuildArch
  #define BuildArch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef WebView2Bootstrapper
  #define WebView2Bootstrapper "..\artifacts\MicrosoftEdgeWebView2Setup.exe"
#endif
#ifndef VcRedist
  #define VcRedist "..\artifacts\VC-Redist.exe"
#endif

#if BuildArch == "x64"
  #define AllowedArchitectures "x64compatible and not arm64"
#elif BuildArch == "ARM64"
  #define AllowedArchitectures "arm64"
#else
  #error BuildArch must be x64 or ARM64
#endif

[Setup]
AppId={{53B67AB8-A64B-4097-BBC7-A5D1F5129A04}
AppName=AM Native
AppVersion={#AppVersion}
AppPublisher=AM Native contributors
SetupIconFile=..\AmNative\Assets\am-native.ico
DefaultDirName={autopf}\AM Native
MinVersion=10.0.19041
DefaultGroupName=AM Native
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installers
OutputBaseFilename=AM-Native-Setup-{#BuildArch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#AllowedArchitectures}
ArchitecturesInstallIn64BitMode={#AllowedArchitectures}
PrivilegesRequired=admin
UninstallDisplayIcon={app}\AmNative.exe
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#WebView2Bootstrapper}"; DestName: "MicrosoftEdgeWebView2Setup.exe"; Flags: dontcopy
Source: "{#VcRedist}"; DestName: "VC-Redist.exe"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AM Native"; Filename: "{app}\AmNative.exe"
Name: "{autodesktop}\AM Native"; Filename: "{app}\AmNative.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AmNative.exe"; Description: "Launch AM Native"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function RunPrerequisite(const FileName, Parameters, ProductName: String;
  AcceptAlreadyInstalled: Boolean;
  var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{tmp}\' + FileName), Parameters, '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
  begin
    Result := Format('The %s installer could not be started: %s', [ProductName, SysErrorMessage(ResultCode)]);
    Exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True
  else if (ResultCode <> 0) and not (AcceptAlreadyInstalled and (ResultCode = 1638)) then
    Result := Format('The %s installer failed with exit code %d.', [ProductName, ResultCode]);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('VC-Redist.exe');
  Result := RunPrerequisite('VC-Redist.exe', '/install /quiet /norestart',
    'Microsoft Visual C++ Runtime', True, NeedsRestart);
  if Result <> '' then
    Exit;

  ExtractTemporaryFile('MicrosoftEdgeWebView2Setup.exe');
  Result := RunPrerequisite('MicrosoftEdgeWebView2Setup.exe', '/silent /install',
    'Microsoft Edge WebView2 Runtime', False, NeedsRestart);
end;
