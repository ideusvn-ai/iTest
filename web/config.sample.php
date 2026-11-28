<?php
// Cấu hình trang web iTest. Sao chép file này thành config.php rồi sửa các giá trị bên dưới.
// config.php KHÔNG được đưa lên Git (đã có trong .gitignore).
return [
    'site' => [
        'name'    => 'iTest',
        // Địa chỉ gốc của trang, không có dấu / ở cuối. Dùng để tạo link trong email.
        'url'     => 'https://itest.example.vn',
        'support' => 'hotro@example.vn',
        'version' => '1.2.0',           // phiên bản bộ cài đang đặt trong storage/downloads
    ],
    'db' => [
        'host' => 'localhost',
        'port' => 3306,
        'name' => 'itest',
        'user' => 'itest',
        'pass' => 'ĐỔI-MẬT-KHẨU',
    ],
    'mail' => [
        // 'smtp' (khuyên dùng) hoặc 'mail' (hàm mail() của máy chủ).
        'driver'    => 'smtp',
        'host'      => 'smtp.example.vn',
        'port'      => 587,
        'secure'    => 'tls',           // 'tls' (STARTTLS, cổng 587), 'ssl' (cổng 465) hoặc '' (không mã hoá)
        'user'      => 'no-reply@example.vn',
        'pass'      => 'ĐỔI-MẬT-KHẨU',
        'from'      => 'no-reply@example.vn',
        'from_name' => 'iTest',
    ],
    'cert' => [
        // Khoá bí mật dùng để ký chứng chỉ. Đặt NGOÀI thư mục web nếu máy chủ cho phép.
        'private_key' => __DIR__ . '/storage/keys/itest-cert-private.pem',
        // Số ngày chứng chỉ có hiệu lực kể từ ngày cấp. 0 = không có hạn.
        'valid_days'  => 365,
    ],
    'downloads' => [
        // Thư mục chứa các file cài đặt. Đặt NGOÀI thư mục web nếu máy chủ cho phép.
        'dir'   => __DIR__ . '/storage/downloads',
        // Tên file trong thư mục trên => tên hiển thị.
        'files' => [
            'iTest-Server-Setup.exe' => 'iTest-Server – máy giám thị',
            'iTest-Editor-Setup.exe' => 'iTest-Editor – máy giáo viên soạn đề',
            'iTest-Setup.exe'        => 'iTest – máy thí sinh',
        ],
        'link_hours' => 24,             // link tải có hiệu lực trong bao nhiêu giờ
    ],
    'limits' => [
        'cert_reissue'    => 3,         // số lần lấy lại chứng chỉ tối đa…
        'cert_days'       => 30,        // …trong số ngày này
        'download_links'  => 5,         // số lần xin link tải mới tối đa…
        'download_hours'  => 24,        // …trong số giờ này
        'login_failures'  => 8,         // số lần đăng nhập sai tối đa trong 15 phút (theo email và theo địa chỉ IP)
        'registrations'   => 5,         // số tài khoản tối đa một địa chỉ IP được tạo trong 1 giờ
    ],
];
