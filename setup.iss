[Setup]
AppName=TokenVault
AppVersion=1.1.4
AppPublisher=TokenVault Dev (自签名开发版)
DefaultDirName={autopf}\TokenVault
DefaultGroupName=TokenVault
OutputBaseFilename=TokenVault-Setup-1.1.4
OutputDir=dist
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=assets\icon.ico
UninstallDisplayIcon={app}\TokenVault.exe
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\TokenVault"; Filename: "{app}\TokenVault.exe"
Name: "{autodesktop}\TokenVault"; Filename: "{app}\TokenVault.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TokenVault.exe"; Description: "启动 TokenVault"; Flags: nowait postinstall skipifsilent
