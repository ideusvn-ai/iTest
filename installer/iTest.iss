; Bộ cài iTest – máy thí sinh
#define AppName "iTest"
#define AppExe "iTest.exe"
#define AppDir "iTest"

[Setup]
AppId={{6B1C2F0E-7A41-4C52-9C3B-1E5D8A7F1101}
AppName={#AppName}
DefaultDirName={autopf}\iTest\iTest
OutputBaseFilename=iTest-Setup

#include "common.iss"

[Run]
; Mở cổng UDP 47800 để nhận tín hiệu dò phòng thi
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""iTest - do phong thi"" dir=in action=allow protocol=UDP localport=47800 program=""{app}\{#AppExe}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""iTest - ket noi"" dir=out action=allow protocol=TCP remoteport=47801 program=""{app}\{#AppExe}"""; Flags: runhidden

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""iTest - do phong thi"""; Flags: runhidden; RunOnceId: "fw1"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""iTest - ket noi"""; Flags: runhidden; RunOnceId: "fw2"
