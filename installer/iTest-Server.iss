; Bộ cài iTest-Server – máy giám thị
#define AppName "iTest-Server"
#define AppExe "iTest-Server.exe"
#define AppDir "iTest-Server"

[Setup]
AppId={{6B1C2F0E-7A41-4C52-9C3B-1E5D8A7F1102}
AppName={#AppName}
DefaultDirName={autopf}\iTest\iTest-Server
OutputBaseFilename=iTest-Server-Setup

#include "common.iss"

[Run]
; Mở cổng TCP 47801 cho phiên làm bài và cho phép phát UDP 47800
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""iTest-Server - phien thi"" dir=in action=allow protocol=TCP localport=47801 program=""{app}\{#AppExe}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""iTest-Server - quang ba"" dir=out action=allow protocol=UDP remoteport=47800 program=""{app}\{#AppExe}"""; Flags: runhidden

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""iTest-Server - phien thi"""; Flags: runhidden; RunOnceId: "fw1"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""iTest-Server - quang ba"""; Flags: runhidden; RunOnceId: "fw2"
