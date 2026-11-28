<?php
// Cấu hình cho bộ chạy thử tự động (tests/run.sh): SQLite thay MySQL, email ghi ra file thay vì gửi đi.
$tmp = getenv('ITEST_TEST_DIR') ?: sys_get_temp_dir() . '/itest-web-test';
return [
    'site' => ['name' => 'iTest', 'url' => getenv('ITEST_TEST_URL') ?: 'http://127.0.0.1:8765', 'support' => 'hotro@example.vn', 'version' => '9.9.9'],
    'db' => ['driver' => 'sqlite-test', 'file' => $tmp . '/test.sqlite'],
    'mail' => ['driver' => 'file', 'dir' => $tmp . '/mail', 'from' => 'no-reply@example.vn', 'from_name' => 'iTest'],
    'cert' => ['private_key' => getenv('ITEST_TEST_KEY') ?: $tmp . '/key.pem', 'valid_days' => 365],
    'downloads' => ['dir' => $tmp . '/downloads', 'link_hours' => 24, 'files' => [
        'iTest-Server-Setup.exe' => 'iTest-Server – máy giám thị', 'iTest-Editor-Setup.exe' => 'iTest-Editor – máy giáo viên soạn đề', 'iTest-Setup.exe' => 'iTest – máy thí sinh']],
    'limits' => ['cert_reissue' => 2, 'cert_days' => 30, 'download_links' => 2, 'download_hours' => 24, 'login_failures' => 3, 'registrations' => 3],
];
