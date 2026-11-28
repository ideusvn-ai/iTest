<?php
// Khởi động chung cho mọi trang: cấu hình, phiên đăng nhập, cơ sở dữ liệu và các hàm tiện ích.
declare(strict_types=1);

date_default_timezone_set('Asia/Ho_Chi_Minh');
mb_internal_encoding('UTF-8');

require_once __DIR__ . '/db.php';
require_once __DIR__ . '/mailer.php';

// ---------------- Cấu hình ----------------
function cfg(string $path, $default = null)
{
    static $config = null;
    if ($config === null) {
        $file = getenv('ITEST_CONFIG') ?: dirname(__DIR__) . '/config.php';
        if (!is_file($file)) {
            http_response_code(500);
            exit('Trang web chưa được cấu hình: hãy sao chép config.sample.php thành config.php và điền thông tin (xem HUONG-DAN.md).');
        }
        $config = require $file;
    }
    $v = $config;
    foreach (explode('.', $path) as $k) {
        if (!is_array($v) || !array_key_exists($k, $v)) { return $default; }
        $v = $v[$k];
    }
    return $v;
}

function db(): Database
{
    static $db = null;
    if ($db === null) {
        if (cfg('db.driver') === 'sqlite-test') {            // chỉ dùng cho bộ chạy thử tự động
            require_once dirname(__DIR__) . '/tests/SqliteDatabase.php';
            $db = new SqliteDatabase(cfg('db.file'));
        } else {
            try { $db = new MysqliDatabase(cfg('db', [])); }
            catch (Throwable $e) {
                error_log('[iTest] Không kết nối được cơ sở dữ liệu: ' . $e->getMessage());
                http_response_code(503);
                exit('Trang đang bảo trì (không kết nối được cơ sở dữ liệu). Vui lòng thử lại sau.');
            }
        }
    }
    return $db;
}

function mailer(): Mailer
{
    static $m = null;
    return $m ??= new Mailer(cfg('mail', []));
}

// ---------------- Phiên đăng nhập ----------------
function is_https(): bool
{
    return (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off') || (($_SERVER['HTTP_X_FORWARDED_PROTO'] ?? '') === 'https');
}

if (session_status() !== PHP_SESSION_ACTIVE && PHP_SAPI !== 'cli') {
    session_name('itest_sess');
    session_set_cookie_params(['lifetime' => 0, 'path' => '/', 'httponly' => true, 'samesite' => 'Lax', 'secure' => is_https()]);
    session_start();
}
header('X-Content-Type-Options: nosniff');
header('X-Frame-Options: DENY');
header('Referrer-Policy: same-origin');

// ---------------- Tiện ích ----------------
function e(?string $s): string { return htmlspecialchars((string)$s, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'); }
function now(): string { return date('Y-m-d H:i:s'); }
function ago(int $seconds): string { return date('Y-m-d H:i:s', time() - $seconds); }
function client_ip(): string { return substr((string)($_SERVER['REMOTE_ADDR'] ?? ''), 0, 45); }
function site_url(string $path = ''): string { return rtrim((string)cfg('site.url', ''), '/') . '/' . ltrim($path, '/'); }
function is_post(): bool { return ($_SERVER['REQUEST_METHOD'] ?? 'GET') === 'POST'; }

/** Lấy một trường văn bản từ form: bỏ khoảng trắng thừa, ký tự điều khiển, cắt theo độ dài tối đa. */
function field(string $name, int $max = 200): string
{
    $v = $_POST[$name] ?? '';
    if (!is_string($v)) { return ''; }
    $v = preg_replace('/[\x00-\x1F\x7F]+/u', ' ', $v) ?? '';
    $v = trim(preg_replace('/\s+/u', ' ', $v) ?? '');
    return mb_substr($v, 0, $max);
}

function redirect(string $to): never
{
    header('Location: ' . $to, true, 303);
    exit;
}

function flash(string $type, string $message): void { $_SESSION['flash'][] = [$type, $message]; }
function take_flash(): array { $f = $_SESSION['flash'] ?? []; unset($_SESSION['flash']); return $f; }

// ---------------- Chống giả mạo yêu cầu (CSRF) ----------------
function csrf_token(): string
{
    return $_SESSION['csrf'] ??= bin2hex(random_bytes(32));
}
function csrf_field(): string { return '<input type="hidden" name="csrf" value="' . e(csrf_token()) . '">'; }
function csrf_check(): void
{
    $sent = $_POST['csrf'] ?? '';
    if (!is_string($sent) || !hash_equals($_SESSION['csrf'] ?? '', $sent) || $sent === '') {
        http_response_code(400);
        exit('Phiên làm việc đã hết hạn hoặc yêu cầu không hợp lệ. Hãy quay lại trang trước, tải lại và thử lại.');
    }
}

// ---------------- Người dùng ----------------
function current_user(): ?array
{
    static $user = false;
    if ($user === false) {
        $id = (int)($_SESSION['uid'] ?? 0);
        $user = $id > 0 ? db()->one('SELECT * FROM users WHERE id = ?', [$id]) : null;
        if ($id > 0 && $user === null) { unset($_SESSION['uid']); }
    }
    return $user;
}

function require_login(): array
{
    $u = current_user();
    if ($u === null) {
        flash('info', 'Vui lòng đăng nhập để tiếp tục.');
        redirect('login.php');
    }
    return $u;
}

function login_user(int $id): void
{
    session_regenerate_id(true);
    $_SESSION['uid'] = $id;
    unset($_SESSION['csrf']);
    db()->exec('UPDATE users SET last_login_at = ? WHERE id = ?', [now(), $id]);
}

// ---------------- Giới hạn tần suất ----------------
function record_event(string $kind, string $subject = ''): void
{
    db()->exec('INSERT INTO events (kind, subject, ip, created_at) VALUES (?, ?, ?, ?)', [$kind, mb_substr($subject, 0, 190), client_ip(), now()]);
    if (random_int(1, 200) === 1) { db()->exec('DELETE FROM events WHERE created_at < ?', [ago(7 * 86400)]); }
}
function count_events(string $kind, int $withinSeconds, ?string $subject = null, ?string $ip = null): int
{
    $sql = 'SELECT COUNT(*) FROM events WHERE kind = ? AND created_at > ?';
    $params = [$kind, ago($withinSeconds)];
    if ($subject !== null) { $sql .= ' AND subject = ?'; $params[] = $subject; }
    if ($ip !== null) { $sql .= ' AND ip = ?'; $params[] = $ip; }
    return (int)db()->value($sql, $params);
}

/** Kiểm tra mật khẩu mới; trả về thông báo lỗi hoặc '' nếu đạt. */
function password_problem(string $pw, string $confirm): string
{
    if (strlen($pw) < 8) { return 'Mật khẩu phải có ít nhất 8 ký tự.'; }
    if (strlen($pw) > 200) { return 'Mật khẩu quá dài.'; }
    if ($pw !== $confirm) { return 'Hai lần nhập mật khẩu không giống nhau.'; }
    return '';
}
