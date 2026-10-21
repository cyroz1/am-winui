#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef BuildArch
  #define BuildArch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{53B67AB8-A64B-4097-BBC7-A5D1F5129A04}
AppName=AM Native
AppVersion={#AppVersion}
AppPublisher=AM Native contributors
SetupIconFile=..\AmNative\Assets\am-native.ico
DefaultDirName={autopf}\AM Native
DefaultGroupName=AM Native
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installers
OutputBaseFilename=AM-Native-Setup-{#BuildArch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\AmNative.exe
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AM Native"; Filename: "{app}\AmNative.exe"
Name: "{autodesktop}\AM Native"; Filename: "{app}\AmNative.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AmNative.exe"; Description: "Launch AM Native"; Flags: nowait postinstall skipifsilent
