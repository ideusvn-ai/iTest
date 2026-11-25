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
   **Artifacts → iTest-Setup-<phiên bản>**, bên trong có `iTest-Setup.exe`, `iTest-Server-Setup.exe`, `iTest-Editor-Setup.exe`.
3. Số phiên bản khai báo ở **một nơi duy nhất**: thẻ `<Version>` trong `Directory.Build.props`. Để phát hành bản chính thức,
   sửa số đó (nếu cần) rồi tạo tag **trùng số phiên bản** và đẩy lên (tag khác số phiên bản sẽ bị workflow từ chối):
   ```
   git tag v1.1.0
   git push origin v1.1.0
   ```
   Workflow sẽ tạo **Release** đính kèm 3 file Setup. Đường dẫn tải cố định dạng
   `https://github.com/<tài-khoản>/<repo>/releases/latest/download/iTest-Setup.exe`.
4. **Trang web giới thiệu & tải xuống** (`website/index.html`) được tự đăng lên GitHub Pages.
   Lần đầu cần bật: **Settings → Pages → Build and deployment → Source: GitHub Actions**.
   Các nút tải trên trang tự trỏ về Release của repo.

## Tự build trên máy Windows

Cần: Windows 10/11, [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0), [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
$p = @('-c','Release','-r','win-x64','--self-contained','true')   # không dùng PublishSingleFile: WPF trên .NET 11 không mở được khi gộp một-file
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
Dùng nút *Tải file Excel mẫu* trong Editor. Cột: `Loại | Nội dung | A | B | C | D | Đáp án | Điểm`.

| Loại | Ý nghĩa | Cột Đáp án |
|---|---|---|
| `TN` | Trắc nghiệm 1 đáp án | `B` |
| `NDA` | Nhiều đáp án đúng (chỉ được điểm khi chọn đúng và đủ) | `A,C` |
| `DS2` | Trắc nghiệm đúng/sai: một mệnh đề, 2 đáp án A. Đúng, B. Sai | `Đ` hoặc `S` |
| `DS` | Đúng/Sai nhiều ý (A–D là các ý a–d) | `Đ,S,Đ,S` |
| `TL` | Tự luận | hướng dẫn chấm |

Câu Đúng/Sai 4 ý tính điểm như đề thi tốt nghiệp THPT: đúng 1 ý 10%, 2 ý 25%, 3 ý 50%, 4 ý 100% điểm câu.

### Công thức toán
Viết công thức LaTeX giữa hai dấu `$`, ví dụ `Tính $\frac{1}{2}+\sqrt{x^2+1}$`, hoặc bấm **∑ Công thức toán…** trong Editor
để chọn mẫu (phân số, căn, mũ, tích phân, hệ phương trình…) có xem trước. Công thức hiển thị đẹp trên đề của thí sinh và trong file PDF.

### Xuất đề PDF
Trong Editor bấm **Xuất đề PDF…**: đề in với tiêu đề **BỘ ĐỀ THI MẪU** (Sở/Trường, môn, năm học, thời gian, số trang),
đúng thứ tự đã soạn, không có mã đề và ô họ tên thí sinh, kèm trang **đáp án và hướng dẫn chấm** nếu chọn.

### Nhập nhanh trong Editor
`Tab` / `Shift+Tab`: đi qua nội dung → phương án A–D → điểm; `Tab` ở ô cuối sang câu sau ·
`Ctrl+Space` trong ô phương án: đánh dấu đáp án đúng (câu đúng/sai: Đúng → Sai → bỏ chọn) · `Ctrl+Enter`: câu sau.

## iTest-Server
- **Chỉ một máy chủ trong mạng**: khi mở, iTest-Server kiểm tra mạng LAN vài giây. Nếu đã có máy chủ khác đang mở phòng thi
  (cùng workgroup), máy mới bị chặn với thông báo lỗi và máy chủ đang mở phòng nhận cảnh báo trong nhật ký (thẻ *CẢNH BÁO*).
  Mỗi máy chỉ chạy được một iTest-Server.
- **Khôi phục sau sự cố**: trạng thái phòng thi được ghi mỗi 5 giây vào `Documents\iTest-Server\SaoLuu\<phòng>\trang-thai.json`.
  Nếu máy chủ tắt đột ngột, mở lại iTest-Server sẽ thấy *Khôi phục phòng thi*: các máy thí sinh tự kết nối lại và làm tiếp
  đúng bài đã lưu; giờ thi được **tạm dừng** cho tới khi giám thị bấm *Tiếp tục*.
- **Xuất điểm cho SMAS / vnEdu**: sau khi thu bài, bấm *Xuất điểm cho SMAS / vnEdu…* để tạo file điểm theo lớp,
  hoặc chọn file mẫu nhập điểm tải từ phần mềm quản lý trường — iTest tìm cột Họ và tên / Ngày sinh / Mã học sinh,
  điền điểm vào cột bạn chọn và báo các học sinh không khớp. Chỉ có điểm phần khách quan (máy chấm).
  Mẫu của mỗi phần mềm có thể thay đổi theo năm, hãy kiểm tra file trước khi nhập.
- **Phát hiện gian lận**: máy thí sinh báo về máy chủ (thẻ *GIAN LẬN* trong nhật ký, dấu ⚠ ở cột kỷ luật) khi phát hiện
  nhiều màn hình, phiên Remote Desktop, máy ảo, phần mềm điều khiển từ xa (TeamViewer, UltraViewer, AnyDesk…);
  trình duyệt, ứng dụng nhắn tin, trợ lý AI, công cụ chụp màn hình bị đóng khi bắt đầu thi và mỗi 8 giây.
  Cửa sổ bài thi được chặn quay/chụp màn hình. Máy chỉ báo, giám thị là người quyết định xử lý.

## Thử tải trước kỳ thi
Tải `iTest-LoadTest.zip` trong mục Artifacts/Releases, chạy trên một máy khác trong cùng mạng:
```
iTest-LoadTest.exe --clients 60 --drop 5 --think 3      # tự tìm phòng thi
iTest-LoadTest.exe --host 192.168.1.10 --clients 120
```
Mỗi máy giả lập (LT001…) vào phòng, làm bài ngẫu nhiên mọi loại câu, gửi nhịp tim, mất mạng ngẫu nhiên rồi nối lại
và nộp khi giám thị thu bài. Màn hình in số máy kết nối, số câu trả lời đã gửi, độ trễ ping trung bình/lớn nhất.

## Kiểm thử tự động
- `dotnet test tests/iTest.Core.Tests` — phần lõi: chia điểm, trộn đề, chấm mọi loại câu, kiểm tra câu trả lời nhận từ máy thí sinh,
  quy định thời gian nộp và chống chiếm phiên, gói `.itz` (mật khẩu, chống sửa, kiểm tra cấu trúc, dấu vân tay), ghi file an toàn theo thứ tự,
  bộ gõ TELEX/VNI và kết nối mã hoá (giới hạn hàng đợi, timeout).
- `dotnet test tests/iTest.Windows.Tests` — chỉ chạy trên Windows: nhập câu hỏi từ file Word thật, xuất điểm (không ghép nhầm học sinh trùng tên,
  tên sheet không trùng), gói kết quả ZIP, và **một phòng thi chạy thật** với máy thí sinh giả: nộp sớm bị từ chối, máy lạ không chiếm được phiên,
  thu bài chờ bản cuối, xác nhận sau khi lưu, bài đã chốt không bị ghi đè, điểm chỉ gửi khi được phép.

GitHub Actions chạy cả hai bộ trước mỗi lần build; lỗi kiểm thử sẽ dừng việc tạo bộ cài. Báo cáo kiểm thử (`.trx`) được lưu ở
**Artifacts → BaoCao-KiemThu** của mỗi lần chạy.

## Ký số các file .exe
1. Có chứng thư ký mã (mua chứng thư OV/EV, hoặc dùng Azure Trusted Signing) dạng `.pfx`.
   Dùng nội bộ trong trường: chạy `installer/new-selfsigned-cert.ps1 -Password "..."` để tạo chứng thư tự ký,
   rồi cài file `.cer` vào *Trusted Root* và *Trusted Publishers* của các máy phòng thi.
2. Vào **Settings → Secrets and variables → Actions** của repo, tạo secret `CODESIGN_PFX_BASE64`
   (nội dung file `.pfx.base64.txt`) và `CODESIGN_PASSWORD`.
3. Từ lần build sau, các file `iTest.exe`, `iTest-Server.exe`, `iTest-Editor.exe` và 3 bộ cài được ký SHA-256 có dấu thời gian.
   Không có secret thì bước ký được bỏ qua.

## Mạng và bảo mật
- Máy chủ quảng bá phòng thi qua **UDP 47800**; phiên làm bài dùng **TCP 47801**. Bộ cài tự mở các cổng này trong Windows Firewall.
- Máy thí sinh chỉ thấy phòng thi **cùng workgroup** Windows. Nếu không dò được (mạng chặn broadcast), nhập IP máy chủ ở ô “Hoặc nhập địa chỉ máy chủ”.
- **Đường truyền LAN được mã hoá & có xác thực bằng mã phòng**: mỗi kết nối trao đổi khoá ECDH P-256 tạm thời,
  mọi tin nhắn mã hoá AES-256-GCM, có bộ đếm chống phát lại. iTest-Server sinh **mã phòng thi 4 số** (hiển thị trên bảng
  điều khiển); giám thị đọc cho cả phòng, thí sinh nhập khi vào phòng. Mã phòng được trộn vào khoá phiên, nên **máy chủ giả
  không có mã phòng sẽ không lừa được máy thí sinh**, và máy nhập sai mã không vào được. Người nghe lén không đọc được đề/bài làm.
- **Chống chiếm phiên**: khi đăng ký, mỗi máy thí sinh được máy chủ cấp một token phiên ngẫu nhiên (lưu trên máy đó). Số báo danh đã có
  bài làm thì **chỉ máy giữ đúng token** mới vào lại được — kể cả khi phiên đang “mất kết nối”. Máy khác, hoặc máy đã mất token, phải được
  giám thị bấm **Chuyển máy**; khi đó token cũ hết hiệu lực và máy mới nhận token mới.
- **Máy chủ không tin máy thí sinh**: mọi câu trả lời được đối chiếu với đề (đúng số câu, đúng loại câu, phương án trong phạm vi, độ dài có giới hạn);
  quy định “chỉ nộp trong N phút cuối” do máy chủ kiểm tra theo đồng hồ của mình; khi giám thị chưa cho xem điểm thì **không con số nào**
  (điểm, số câu đúng/sai) được gửi xuống máy thí sinh.
- **Nộp bài**: tin nộp mang theo toàn bộ bài làm cuối cùng. Khi thu bài, máy chủ chờ bản cuối của từng máy tối đa 10 giây. Máy thí sinh chỉ
  báo “Đã nộp bài thành công” sau khi máy chủ xác nhận **đã ghi bài xuống đĩa**; bài đã chốt không bị các tin gửi lại ghi đè.
- **Bộ đề `.itz` được kiểm tra khi mở và khi đóng gói**: cấu trúc, mã câu trùng, điểm không hợp lệ, đáp án ngoài phạm vi, tên và kích thước
  file ảnh/âm thanh (tối đa 25 MB mỗi file, 200 MB cả bộ).
- File `.itz` được mã hoá **AES-256-GCM**, khoá sinh từ mật khẩu bằng PBKDF2-SHA256 (200.000 vòng); file bị sửa sẽ không mở được.
- Chế độ thi: toàn màn hình, luôn nằm trên cùng, chặn phím Windows, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4 và tạm tắt Task Manager.
  **Ctrl+Alt+Del không thể chặn** bằng phần mềm thường; mọi lần cửa sổ mất tiêu điểm đều được báo về máy chủ.
- Bài làm được lưu ở máy chủ sau mỗi thao tác, sao lưu mỗi 30 giây vào `Documents\iTest-Server\SaoLuu\`, và lưu tạm trên máy thí sinh.
  Mọi bản lưu được ghi ra file tạm rồi mới thay file chính, theo đúng thứ tự (bản cũ không đè bản mới, không có file ghi dở khi mất điện).

## Khi có sự cố trong buổi thi
- **Máy thí sinh mất mạng**: máy tự kết nối lại ở mọi trạng thái (phòng chờ, đang làm bài, đã nộp). Bài làm trên máy thí sinh là bản mới nhất
  và được gửi lại đầy đủ khi có mạng; máy chủ bù giờ cho thời gian mất kết nối. Hết giờ đúng lúc đang mất mạng thì máy chờ thêm tối đa 2 phút rồi mới tự nộp.
- **Máy thí sinh bị khởi động lại** (mất điện, treo máy): mở lại iTest, vào đúng phòng thi và đăng ký lại đúng số báo danh — máy dùng token
  đã lưu để nhận lại phiên, bài làm và số lượt nghe đã dùng. Nếu lúc đó bài đã nộp nhưng chưa được xác nhận, bài được gửi lại ngay.
  Nếu máy mất cả token (cài lại máy, đổi tài khoản Windows) hoặc phải đổi sang máy khác, giám thị bấm **Chuyển máy**.
- **Nộp bài lúc máy chủ chưa cho nộp** (đồng hồ hai máy lệch, hoặc máy tự nộp sau khi mất mạng lâu): máy chủ từ chối, bài tự mở lại để làm tiếp với giờ đúng.
- **Đã nộp bài nhưng máy chủ không còn liên lạc được**: sau 1 phút (bài đã được xác nhận) hoặc 5 phút (bài chưa được xác nhận, còn giữ trên máy)
  máy thí sinh tự mở khoá và hiện nút *Thoát ứng dụng* (không bị khoá vô thời hạn). Bài chưa xác nhận sẽ được gửi lại khi mở iTest và vào lại phòng.
- **Khôi phục phòng thi trên máy chủ**: phải chọn đúng file bộ đề đã dùng — máy chủ so dấu vân tay SHA-256 của nội dung đề và ảnh/âm thanh,
  nên bộ đề khác có cùng số câu sẽ bị từ chối.
- **iTest bị tắt đột ngột khi đang khoá máy** (mất điện, treo máy): lần mở iTest kế tiếp tự bật lại Task Manager.
- **Lỗi ngoài dự kiến**: cả ba ứng dụng ghi lỗi vào `%LocalAppData%\<tên ứng dụng>\loi.log` và chạy tiếp thay vì đóng sập. Gửi file này khi cần hỗ trợ.
- **Bộ gõ tiếng Việt** có sẵn (TELEX/VNI) có thể **tắt** — trong Editor ở góc trên phải, trên máy thí sinh ở nút ⚙ — khi soạn/làm bài tiếng Anh
  hoặc khi máy đã bật Unikey. Trong công thức `$...$` bộ gõ tự không bỏ dấu.

## Soạn và nhập câu hỏi
- **Thêm / xoá / đổi thứ tự từng câu**: dùng các nút *＋ Thêm*, *Xoá*, *▲*, *▼* dưới danh sách câu hỏi trong Editor. Câu chỉ di chuyển trong phần của nó
  (trắc nghiệm hoặc tự luận); khi thêm hoặc xoá câu, điểm của phần đó được chia đều lại theo tổng điểm phần.
- **Nhập từ Word** tự nhận loại câu: đánh dấu (gạch chân, tô đỏ hoặc dấu `*`) từ 2 phương án trở lên → *nhiều đáp án đúng*; chỉ có hai phương án
  “Đúng” và “Sai” → *trắc nghiệm đúng/sai*; các ý viết thường `a)` `b)` `c)` `d)` → *Đúng/Sai nhiều ý* (ý được đánh dấu là ý Đúng).
- **Phân tích đề** trong file kết quả có mọi loại câu khách quan, kèm nhận xét: phương án nhiễu nổi bật, phương án đúng hay bị bỏ sót, ý khó nhất.
- **Bài tự luận đánh phách (PDF)**: công thức `$...$` trong đề được vẽ thành công thức thật.

## Giới hạn còn lại
- Công thức MathType/Equation trong file Word nhập vào được giữ ở dạng văn bản; nên soạn lại bằng nút ∑ (LaTeX).
- Công thức do thí sinh gõ trong bài tự luận được in nguyên văn (không vẽ thành công thức).
- Các file `.exe` chưa được ký số nên trình duyệt và Windows còn cảnh báo khi tải; xem mục *Ký số các file .exe*.

## Cấu trúc mã nguồn
```
src/iTest.Core     Mô hình bộ đề, gói .itz (mã hoá), trộn đề, chấm điểm, giao thức mạng, dò phòng, bộ gõ TELEX/VNI
src/iTest.UI       Giao diện dùng chung (màu, nút, hiệu ứng), gắn bộ gõ vào ô nhập
src/iTest.Client   Ứng dụng thí sinh (chế độ khoá máy: KioskGuard.cs)
src/iTest.UI       … và hiển thị công thức LaTeX (RichMath.cs)
src/iTest.Client   … phát hiện gian lận (AntiCheat.cs)
src/iTest.Server   Bảng điều khiển phòng thi (RoomService.cs: khôi phục sự cố), xuất Excel/PDF (Exporter.cs),
                   xuất điểm SMAS/vnEdu (SchoolExport.cs)
src/iTest.Editor   Soạn đề, bản nháp tự lưu (Drafts.cs), nhập Word/Excel (Importer.cs), xuất đề PDF (ExamPdf.cs)
tests/             Kiểm thử tự động phần lõi (xUnit)
tools/             iTest-LoadTest: giả lập nhiều máy thí sinh
installer/         Script Inno Setup cho 3 bộ cài, script ký số (sign.ps1, new-selfsigned-cert.ps1)
website/           Trang giới thiệu & tải xuống
```
