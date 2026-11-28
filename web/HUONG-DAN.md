# Trang web iTest – hướng dẫn cài đặt

Trang web viết bằng PHP thuần và MySQLi, không cần Composer hay thư viện ngoài.

## Trang web làm gì
- Trang chủ giới thiệu phần mềm, có hình động minh hoạ giao diện ba ứng dụng.
- Trường **đăng ký tài khoản** (tên Sở, tên trường, người phụ trách, email). Đăng ký xong, hệ thống gửi một email gồm:
  - **link tải bộ cài**, có hiệu lực 24 giờ;
  - **file chứng chỉ `.itcert`** đính kèm, ghi đúng tên Sở và tên trường đã nhập, có chữ ký số.
- Trang **Tài khoản**: đổi thông tin, đổi mật khẩu, xin link tải mới, lấy lại chứng chỉ. Link tải và chứng chỉ **chỉ gửi qua email** đã đăng ký
  và có **giới hạn số lượt** (chỉnh trong `config.php`).
- Quên mật khẩu: link đặt lại gửi qua email, hiệu lực 1 giờ, dùng một lần.

## Yêu cầu máy chủ
- PHP 8.1 trở lên với các phần mở rộng `mysqli`, `openssl`, `mbstring`, `json` (nên có thêm `intl` để tên file chứng chỉ bỏ dấu đẹp hơn).
- MySQL 5.7+ hoặc MariaDB 10.3+.
- Apache có bật `.htaccess` (hosting thông thường đều có). Với Nginx, xem mục cuối.
- HTTPS. Không chạy trang đăng nhập trên HTTP thường.
- Một hộp thư SMTP để gửi email (ví dụ hộp thư theo tên miền của bạn).

## Các bước
1. **Chép thư mục `web/`** lên máy chủ, làm thư mục gốc của trang (ví dụ `public_html`).
2. **Tạo cơ sở dữ liệu** rồi chạy `schema.sql`:
   ```
   mysql -u <user> -p <database> < schema.sql
   ```
   (hoặc dán nội dung file vào phpMyAdmin → SQL).
3. **Tạo `config.php`**: sao chép `config.sample.php` thành `config.php` và điền địa chỉ trang, thông tin MySQL, thông tin SMTP.
4. **Đặt khoá ký chứng chỉ**: chép file `itest-cert-private.pem` (file khoá bí mật bạn đang giữ) vào `storage/keys/`.
   Đây là khoá đi cặp với khoá công khai đã nhúng trong phần mềm; **không tạo khoá mới** trừ khi bạn cũng build lại phần mềm.
   Đặt quyền file `600`. Ai có file này đều cấp được chứng chỉ, nên không gửi qua email hay chat và không đưa vào Git.
5. **Đặt bộ cài**: chép `iTest-Setup.exe`, `iTest-Server-Setup.exe`, `iTest-Editor-Setup.exe` (tải ở GitHub → Actions → Artifacts) vào `storage/downloads/`,
   rồi sửa `site.version` trong `config.php` cho đúng phiên bản. Mỗi lần có bản mới chỉ cần chép đè ba file này.
6. **Thử**: mở trang, đăng ký một tài khoản bằng email của bạn, kiểm tra email nhận được, tải bộ cài và cài iTest-Server với file chứng chỉ đính kèm.

## An toàn
- Nếu hosting cho phép, hãy đặt khoá và bộ cài **ngoài thư mục web** rồi sửa `cert.private_key` và `downloads.dir` trong `config.php`.
  Nếu để nguyên trong `storage/`, file `.htaccess` kèm theo đã chặn truy cập trực tiếp (chỉ có tác dụng trên Apache).
- Sau khi cài xong, kiểm tra bằng trình duyệt rằng các địa chỉ sau **không** mở được:
  `/config.php` (trang trắng là đúng), `/storage/keys/itest-cert-private.pem`, `/storage/downloads/iTest-Setup.exe`, `/lib/app.php`.
- Mật khẩu người dùng được băm bằng `password_hash`; link tải và link đặt lại mật khẩu chỉ lưu dạng băm SHA-256 trong cơ sở dữ liệu.
- Mọi form có mã chống giả mạo yêu cầu (CSRF); đăng nhập sai, đăng ký và quên mật khẩu đều bị giới hạn tần suất.

## Những điều cần biết
- **Ai đăng ký cũng nhận được chứng chỉ** ngay qua email, hệ thống không kiểm tra người đăng ký có thật là trường học hay không.
  Email phải có thật (chứng chỉ và link tải chỉ đến qua email). Bạn có thể xem danh sách đăng ký trong bảng `users` và `certificates`.
- **Không thu hồi được chứng chỉ đã cấp** vì phần mềm chạy không cần Internet. Chứng chỉ tự hết hiệu lực theo `cert.valid_days` (mặc định 365 ngày);
  trường lấy lại chứng chỉ mới trong trang Tài khoản.
- Đổi tên Sở hoặc tên trường trong trang Tài khoản sẽ cấp chứng chỉ mới và tính vào số lượt lấy lại (mặc định 3 lượt trong 30 ngày).
- Thay hình minh hoạ bằng ảnh động thật: xem `assets/screens/README.txt`.

## Công cụ dòng lệnh
- `php tools/issue.php "<Sở>" "<Trường>" "<Người phụ trách>" <email> <điện thoại> [số ngày]` — cấp chứng chỉ bằng tay (ví dụ cho Sở/Phòng Giáo dục).
- `php tools/keygen.php --force` — tạo cặp khoá mới. Chỉ dùng khi khoá bí mật bị lộ; sau đó phải dán khoá công khai mới vào
  `src/iTest.Core/Certificate.cs`, build lại phần mềm, và mọi trường phải lấy lại chứng chỉ.
- `bash tests/run.sh` — chạy thử toàn bộ luồng (dùng SQLite và ghi email ra file, không đụng tới MySQL hay hộp thư thật).

## Nginx
Nginx không đọc `.htaccess`. Thêm vào cấu hình máy chủ:
```
location ~ ^/(lib|storage|tools|tests)(/|$) { return 404; }
location ~ ^/(config(\.sample)?\.php|schema\.sql|HUONG-DAN\.md)$ { return 404; }
```
