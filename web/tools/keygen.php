<?php
// Tạo cặp khoá ký chứng chỉ MỚI. Chạy bằng dòng lệnh:  php tools/keygen.php
// Chỉ cần khi muốn thay khoá (ví dụ nghi khoá bí mật bị lộ). Sau khi thay khoá:
//   1. Dán khoá công khai in ra bên dưới vào hằng EmbeddedPublicKey trong src/iTest.Core/Certificate.cs rồi build lại phần mềm.
//   2. Mọi chứng chỉ đã cấp bằng khoá cũ sẽ không dùng được với bản phần mềm mới; các trường phải lấy lại chứng chỉ.
if (PHP_SAPI !== 'cli') { http_response_code(404); exit; }

$dir = dirname(__DIR__) . '/storage/keys';
$file = $dir . '/itest-cert-private.pem';
if (file_exists($file) && !in_array('--force', $argv, true)) {
    fwrite(STDERR, "Đã có khoá tại $file. Thêm --force nếu thật sự muốn tạo khoá mới (khoá cũ được đổi tên, không bị xoá).\n");
    exit(1);
}
$key = openssl_pkey_new(['private_key_type' => OPENSSL_KEYTYPE_RSA, 'private_key_bits' => 3072]);
if (!$key || !openssl_pkey_export($key, $pem)) { fwrite(STDERR, "Không tạo được khoá: " . openssl_error_string() . "\n"); exit(1); }
if (!is_dir($dir)) { mkdir($dir, 0700, true); }
if (file_exists($file)) { rename($file, $file . '.' . date('Ymd-His') . '.bak'); }
file_put_contents($file, $pem);
chmod($file, 0600);
echo "Đã lưu khoá bí mật: $file\n\nKhoá công khai (dán vào src/iTest.Core/Certificate.cs):\n\n" . openssl_pkey_get_details($key)['key'] . "\n";
