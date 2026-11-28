<?php
// Cấp chứng chỉ bằng tay (không qua đăng ký trên web), ví dụ cho Sở/Phòng Giáo dục hoặc để thử phần mềm.
//   php tools/issue.php "Sở Giáo dục và Đào tạo X" "Trường THPT Y" "Người phụ trách" email@truong.edu.vn 0900000000 [số ngày hiệu lực]
if (PHP_SAPI !== 'cli') { http_response_code(404); exit; }
require dirname(__DIR__) . '/lib/app.php';
require dirname(__DIR__) . '/lib/service.php';

if ($argc < 6) { fwrite(STDERR, "Cách dùng: php tools/issue.php \"<Sở>\" \"<Trường>\" \"<Người phụ trách>\" <email> <điện thoại> [số ngày]\n"); exit(1); }
$days = isset($argv[6]) ? (int)$argv[6] : (int)cfg('cert.valid_days', 365);
$content = cert_build([
    'id' => 'IT-M-' . date('ymdHis'), 'department' => $argv[1], 'school' => $argv[2], 'contact' => $argv[3], 'email' => $argv[4], 'phone' => $argv[5],
    'issued' => date('Y-m-d'), 'expires' => $days > 0 ? date('Y-m-d', strtotime("+$days days")) : ($days < 0 ? date('Y-m-d', strtotime("$days days")) : ''),
]);
$name = cert_filename($argv[2]);
file_put_contents($name, $content);
echo "Đã tạo $name\n";
