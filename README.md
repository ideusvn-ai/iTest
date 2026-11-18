# iTest – Phần mềm thi trên máy tính cho phòng máy

Bộ ba ứng dụng Windows chạy trong mạng LAN, không cần Internet:

| Ứng dụng | Cài trên | Việc chính |
|---|---|---|
| **iTest** (`iTest.exe`) | Máy thí sinh | Tìm phòng thi, nhập thông tin, làm bài ở chế độ khoá máy, nộp bài |
| **iTest-Server** (`iTest-Server.exe`) | Máy giám thị | Mở phòng, phát đề, giám sát, nhắc nhở, chuyển máy, thu bài, xuất kết quả |
| **iTest-Editor** (`iTest-Editor.exe`) | Máy giáo viên | Tạo bộ đề, soạn và nhập câu hỏi từ Word/Excel, đóng gói `.itz` |

## Tạo bộ cài .exe bằng GitHub (không cần cài gì trên máy)

1. Tạo một repository mới trên GitHub (ví dụ `itest`) và đẩy toàn bộ thư mục này lên nhánh `main`.
2. Vào tab **Actions**: workflow **Build iTest** tự chạy. Khoảng 5–10 phút sau, mở lần chạy đó và tải mục
   **Artifacts → iTest-Setup-1.0.0**, bên trong có `iTest-Setup.exe`, `iTest-Server-Setup.exe`, `iTest-Editor-Setup.exe`.
3. Để phát hành bản chính thức, tạo tag phiên bản rồi đẩy lên:
   ```
   git tag v1.0.0
   git push origin v1.0.0
   ```
   Workflow sẽ tạo **Release** đính kèm 3 file Setup. Đường dẫn tải cố định dạng
   `https://github.com/<tài-khoản>/<repo>/releases/latest/download/iTest-Setup.exe`.
4. **Trang web giới thiệu & tải xuống** (`website/index.html`) được tự đăng lên GitHub Pages.
   Lần đầu cần bật: **Settings → Pages → Build and deployment → Source: GitHub Actions**.
   Các nút tải trên trang tự trỏ về Release của repo.

## Tự build trên máy Windows

Cần: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
$p = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')
dotnet publish src/iTest.Client/iTest.Client.csproj @p -o publish/iTest
dotnet publish src/iTest.Server/iTest.Server.csproj @p -o publish/iTest-Server
dotnet publish src/iTest.Editor/iTest.Editor.csproj @p -o publish/iTest-Editor
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\iTest.iss
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\iTest-Server.iss
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\iTest-Editor.iss
```
Bộ cài nằm trong thư mục `dist\`. Có thể mở `iTest.sln` bằng Visual Studio 2022 để chạy thử từng ứng dụng.

## Cách dùng nhanh

1. **Giáo viên** mở iTest-Editor → *Tạo bộ đề mới* (kỳ thi, môn, năm học, người soạn, số câu, điểm, mật khẩu) →
   soạn hoặc *Nhập từ Word/Excel* → khi đủ câu và tổng điểm bằng 10 thì *Đóng gói bộ câu hỏi* ra file `.itz`.
2. **Giám thị** mở iTest-Server → nhập Sở GD&ĐT, Trường, Phòng thi, Giám thị → chọn file `.itz`, nhập mật khẩu → *Mở phòng thi*.
3. **Thí sinh** mở iTest → chọn phòng → nhập thông tin → chờ giám thị *Phát đề & bắt đầu*.
4. Cuối giờ: *Thu bài tất cả* (chỉ mở trong số phút cuối theo quy định) → *Xuất kết quả* (.zip) → *Đóng phòng*.

### Quy tắc soạn file Word để nhập
- Mỗi câu bắt đầu bằng `Câu 1.` hoặc `Câu 1:`; có thể ghi điểm ngay sau: `Câu 37 (0,5 điểm).`
- Phương án bắt đầu bằng `A.` `B.` `C.` `D.` (một hoặc nhiều phương án trên một dòng, hoặc trong bảng).
- Đáp án đúng: **gạch chân**, **tô màu đỏ**, hoặc thêm dấu `*` trước chữ cái (`*C.`).
- Dòng tiêu đề có chữ `TỰ LUẬN` chuyển sang phần tự luận. Ảnh đặt trong đoạn của câu hỏi được giữ lại.

### File Excel
Dùng nút *Tải file Excel mẫu* trong Editor. Cột: `Loại (TN/TL) | Nội dung | A | B | C | D | Đáp án | Điểm`.

## Mạng và bảo mật
- Máy chủ quảng bá phòng thi qua **UDP 47800**; phiên làm bài dùng **TCP 47801**. Bộ cài tự mở các cổng này trong Windows Firewall.
- Máy thí sinh chỉ thấy phòng thi **cùng workgroup** Windows. Nếu không dò được (mạng chặn broadcast), nhập IP máy chủ ở ô “Hoặc nhập địa chỉ máy chủ”.
- File `.itz` được mã hoá **AES-256-GCM**, khoá sinh từ mật khẩu bằng PBKDF2-SHA256 (200.000 vòng); file bị sửa sẽ không mở được.
- Chế độ thi: toàn màn hình, luôn nằm trên cùng, chặn phím Windows, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4 và tạm tắt Task Manager.
  **Ctrl+Alt+Del không thể chặn** bằng phần mềm thường; mọi lần cửa sổ mất tiêu điểm đều được báo về máy chủ.
- Bài làm được lưu ở máy chủ sau mỗi thao tác, sao lưu mỗi 30 giây vào `Documents\iTest-Server\SaoLuu\`, và lưu tạm trên máy thí sinh.

## Giới hạn của phiên bản 1.0
- Dữ liệu trong mạng LAN chưa được mã hoá đường truyền (chỉ file bộ đề được mã hoá). Nên dùng mạng riêng của phòng máy.
- Phần mềm diệt virus có thể cảnh báo hook bàn phím của iTest; nên ký số (code signing) các file .exe trước khi triển khai rộng.
- Chưa hỗ trợ soạn công thức dạng MathType/Equation; công thức nhập từ Word được giữ ở dạng văn bản.

## Cấu trúc mã nguồn
```
src/iTest.Core     Mô hình bộ đề, gói .itz (mã hoá), trộn đề, chấm điểm, giao thức mạng, dò phòng, bộ gõ TELEX/VNI
src/iTest.UI       Giao diện dùng chung (màu, nút, hiệu ứng), gắn bộ gõ vào ô nhập
src/iTest.Client   Ứng dụng thí sinh (chế độ khoá máy: KioskGuard.cs)
src/iTest.Server   Bảng điều khiển phòng thi (RoomService.cs), xuất Excel/PDF (Exporter.cs)
src/iTest.Editor   Soạn đề, bản nháp tự lưu (Drafts.cs), nhập Word/Excel (Importer.cs)
installer/         Script Inno Setup cho 3 bộ cài
website/           Trang giới thiệu & tải xuống
```
