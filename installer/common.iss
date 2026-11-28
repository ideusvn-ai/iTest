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

#ifdef NeedCertificate
// ---------------- Chứng chỉ sử dụng ----------------
// iTest-Server và iTest-Editor lấy tên Sở, tên trường từ chứng chỉ (.itcert) do trang web iTest cấp cho trường.
// Bộ cài yêu cầu chọn file chứng chỉ; sau khi chép xong ứng dụng, chính ứng dụng kiểm tra chữ ký số và hạn dùng của file.
// Cài im lặng: truyền /CERT="đường dẫn file" (không truyền thì ứng dụng sẽ hỏi chứng chỉ khi mở lần đầu).
var
  CertPage: TInputFileWizardPage;
  CertPath: String;

function InstalledCertPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\iTest\chung-chi.itcert');
end;

function LooksLikeCertificate(const FileName: String): Boolean;
var
  S: AnsiString;
begin
  Result := LoadStringFromFile(FileName, S) and (Length(S) < 20000) and (Pos('iTest-Certificate-1', String(S)) > 0);
end;

procedure InitializeWizard();
begin
  CertPage := CreateInputFilePage(wpSelectDir, 'Chứng chỉ sử dụng', 'Chọn file chứng chỉ (.itcert) của trường',
    'Tên Sở và tên trường trong phần mềm được lấy từ chứng chỉ sử dụng. File chứng chỉ được gửi qua email khi trường đăng ký tài khoản trên trang web iTest, và có thể lấy lại trong mục Tài khoản của trang web.');
  CertPage.Add('File chứng chỉ:', 'Chứng chỉ iTest (*.itcert)|*.itcert|Tất cả các file|*.*', '.itcert');
  CertPath := ExpandConstant('{param:CERT|}');
  CertPage.Values[0] := CertPath;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CertPage.ID) and WizardSilent();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> CertPage.ID) or WizardSilent() then Exit;
  CertPath := Trim(CertPage.Values[0]);
  if CertPath = '' then
  begin
    if FileExists(InstalledCertPath()) then
      Result := MsgBox('Máy này đã có chứng chỉ từ lần cài trước. Dùng tiếp chứng chỉ đó?', mbConfirmation, MB_YESNO) = IDYES
    else
    begin
      MsgBox('Vui lòng chọn file chứng chỉ (.itcert) của trường.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if (not FileExists(CertPath)) or (not LooksLikeCertificate(CertPath)) then
  begin
    MsgBox('File đã chọn không phải chứng chỉ iTest. Hãy chọn file .itcert nhận được qua email.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep <> ssPostInstall) or (CertPath = '') or (not FileExists(CertPath)) then Exit;
  Code := -1;
  if Exec(ExpandConstant('{app}\{#AppExe}'), '--check-cert "' + CertPath + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
  begin
    ForceDirectories(ExtractFileDir(InstalledCertPath()));
    FileCopy(CertPath, InstalledCertPath(), False);
    Log('Da cai chung chi: ' + CertPath);
  end
  else
  begin
    Log('Chung chi khong dung duoc, ma: ' + IntToStr(Code));
    if Code = 3 then
      SuppressibleMsgBox('Chứng chỉ đã chọn đã hết hạn. Đăng nhập trang web iTest để lấy lại chứng chỉ mới; ứng dụng sẽ hỏi chứng chỉ khi mở.', mbError, MB_OK, IDOK)
    else
      SuppressibleMsgBox('Chứng chỉ đã chọn không dùng được (file đã bị sửa hoặc không do iTest cấp). Ứng dụng sẽ hỏi lại chứng chỉ khi mở.', mbError, MB_OK, IDOK);
  end;
end;
#endif

