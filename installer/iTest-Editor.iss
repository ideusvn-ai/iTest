; Bộ cài iTest-Editor – máy giáo viên soạn đề
#define AppName "iTest-Editor"
#define AppExe "iTest-Editor.exe"
#define AppDir "iTest-Editor"

[Setup]
AppId={{6B1C2F0E-7A41-4C52-9C3B-1E5D8A7F1103}
AppName={#AppName}
DefaultDirName={autopf}\iTest\iTest-Editor
OutputBaseFilename=iTest-Editor-Setup
ChangesAssociations=yes

#include "common.iss"

[Registry]
; Liên kết đuôi .itz với iTest-Editor
Root: HKA; Subkey: "Software\Classes\.itz"; ValueType: string; ValueName: ""; ValueData: "iTest.ExamPackage"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\iTest.ExamPackage"; ValueType: string; ValueName: ""; ValueData: "Bộ đề iTest"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\iTest.ExamPackage\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
