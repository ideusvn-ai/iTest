; Phần dùng chung cho 3 bộ cài iTest. Được #include từ từng file .iss.
; Biên dịch: ISCC.exe /DAppVersion=1.0.0 installer\iTest.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppVersion={#AppVersion}
AppPublisher=iTest
AppPublisherURL=https://github.com/
DefaultGroupName=iTest
DisableProgramGroupPage=yes
OutputDir=..\dist
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.DesktopIconTask=Tạo lối tắt trên màn hình nền
english.LaunchTask=Mở {#AppName} sau khi cài đặt

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"

[Files]
Source: "..\publish\{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchTask}"; Flags: nowait postinstall skipifsilent
