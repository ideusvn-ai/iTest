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
SetupIconFile=..\assets\{#AppDir}.ico
MinVersion=10.0

; Đóng ứng dụng đang chạy trước khi cài (bộ cài tự làm thêm một lần ở phần [Code]).
CloseApplications=yes
RestartApplications=no

[InstallDelete]
; Xoá mọi file còn sót của bản cũ trong thư mục cài trước khi chép bản mới (dữ liệu người dùng nằm ở Documents và AppData, không nằm ở đây).
Type: filesandordirs; Name: "{app}\*"

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

[Code]
// Gỡ bản cũ trước khi cài bản mới: đóng ứng dụng đang chạy, chạy trình gỡ cài đặt của bản đã cài (nếu có) ở chế độ im lặng.
// Bộ đề, bản nháp, bản sao lưu và kết quả thi nằm ở Documents / AppData nên KHÔNG bị xoá.
function OldUninstaller(): String;
var
  Key, S: String;
begin
  Result := '';
  Key := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1');
  if RegQueryStringValue(HKLM, Key, 'UninstallString', S) or
     RegQueryStringValue(HKLM32, Key, 'UninstallString', S) or
     RegQueryStringValue(HKCU, Key, 'UninstallString', S) then
    Result := RemoveQuotes(S);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code, I: Integer;
  Uninst: String;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "{#AppExe}"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Uninst := OldUninstaller();
  if (Uninst <> '') and FileExists(Uninst) then
  begin
    Log('Go ban cu: ' + Uninst);
    Exec(Uninst, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
    // Trình gỡ tự sao chép ra thư mục tạm rồi mới xoá: chờ tới khi file gỡ cài đặt cũ biến mất (tối đa 30 giây).
    I := 0;
    while FileExists(Uninst) and (I < 60) do
    begin
      Sleep(500);
      I := I + 1;
    end;
  end;
end;
