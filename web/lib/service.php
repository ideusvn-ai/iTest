<?php
// Nghiệp vụ: cấp chứng chỉ ký số, tạo link tải có thời hạn và soạn các email gửi cho trường.
declare(strict_types=1);

/**
 * Tạo nội dung file chứng chỉ (.itcert). Định dạng PHẢI khớp với lớp CertificateStore trong iTest.Core:
 * JSON gồm "format", "data" (nội dung JSON, base64) và "sig" (chữ ký RSA-SHA256, PKCS#1 v1.5, trên đúng các byte của nội dung).
 */
function cert_build(array $fields): string
{
    $keyFile = (string)cfg('cert.private_key');
    $pem = is_file($keyFile) ? file_get_contents($keyFile) : false;
    $key = $pem ? openssl_pkey_get_private($pem) : false;
    if (!$key) { throw new RuntimeException('Không đọc được khoá ký chứng chỉ (cert.private_key trong config.php).'); }
    $payload = json_encode([
        'id'         => $fields['id'],
        'department' => $fields['department'],
        'school'     => $fields['school'],
        'contact'    => $fields['contact'],
        'email'      => $fields['email'],
        'phone'      => $fields['phone'],
        'issued'     => $fields['issued'],
        'expires'    => $fields['expires'] ?? '',
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
    if (!openssl_sign($payload, $signature, $key, OPENSSL_ALGO_SHA256)) { throw new RuntimeException('Không ký được chứng chỉ.'); }
    return json_encode(['format' => 'iTest-Certificate-1', 'data' => base64_encode($payload), 'sig' => base64_encode($signature)],
        JSON_PRETTY_PRINT | JSON_THROW_ON_ERROR) . "\n";
}

/** Tên file chứng chỉ không dấu, an toàn cho mọi hệ điều hành và trình thư. */
function cert_filename(string $school): string
{
    $map = ['đ' => 'd', 'Đ' => 'D'];
    $s = strtr($school, $map);
    $s = class_exists('Normalizer') ? preg_replace('/\p{Mn}+/u', '', Normalizer::normalize($s, Normalizer::FORM_D)) : $s;
    $s = trim(preg_replace('/[^A-Za-z0-9]+/', '-', $s) ?? '', '-');
    return 'ChungChi-iTest-' . ($s !== '' ? substr($s, 0, 60) : 'truong') . '.itcert';
}

/**
 * Cấp một chứng chỉ mới cho tài khoản theo thông tin hiện tại và ghi vào bảng certificates.
 * @param string $reason register | reissue | profile
 * @return array ['name' => tên file, 'content' => nội dung, 'id' => số chứng chỉ, 'expires' => 'Y-m-d' hoặc null]
 */
function cert_issue(array $user, string $reason): array
{
    $days = (int)cfg('cert.valid_days', 365);
    $issued = date('Y-m-d');
    $expires = $days > 0 ? date('Y-m-d', strtotime("+$days days")) : null;
    $serial = 1 + (int)db()->value('SELECT COUNT(*) FROM certificates WHERE user_id = ?', [(int)$user['id']]);
    $certId = sprintf('IT-%05d-%02d', (int)$user['id'], $serial);
    $content = cert_build([
        'id' => $certId, 'department' => $user['department'], 'school' => $user['school'], 'contact' => $user['contact_name'],
        'email' => $user['email'], 'phone' => $user['phone'], 'issued' => $issued, 'expires' => $expires ?? '',
    ]);
    db()->exec('INSERT INTO certificates (user_id, cert_id, department, school, reason, issued_on, expires_on, created_at, ip) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)',
        [(int)$user['id'], $certId, $user['department'], $user['school'], $reason, $issued, $expires, now(), client_ip()]);
    return ['name' => cert_filename($user['school']), 'content' => $content, 'id' => $certId, 'expires' => $expires];
}

function cert_latest(int $userId): ?array
{
    return db()->one('SELECT * FROM certificates WHERE user_id = ? ORDER BY id DESC LIMIT 1', [$userId]);
}

/** Số lần còn được lấy lại chứng chỉ trong khoảng thời gian quy định (lần cấp lúc đăng ký không tính). */
function cert_reissues_left(int $userId): int
{
    $used = (int)db()->value("SELECT COUNT(*) FROM certificates WHERE user_id = ? AND reason <> 'register' AND created_at > ?",
        [$userId, ago((int)cfg('limits.cert_days', 30) * 86400)]);
    return max(0, (int)cfg('limits.cert_reissue', 3) - $used);
}

// ---------------- Link tải có thời hạn ----------------
function download_token_create(int $userId): array
{
    $token = bin2hex(random_bytes(24));
    $hours = (int)cfg('downloads.link_hours', 24);
    $expires = date('Y-m-d H:i:s', time() + $hours * 3600);
    db()->exec('INSERT INTO download_tokens (user_id, token_hash, expires_at, created_at) VALUES (?, ?, ?, ?)', [$userId, hash('sha256', $token), $expires, now()]);
    return ['token' => $token, 'url' => site_url('download.php?t=' . $token), 'expires' => $expires, 'hours' => $hours];
}

/** Dòng token còn hạn ứng với mã trong link, hoặc null. */
function download_token_find(string $token): ?array
{
    if (!preg_match('/^[a-f0-9]{48}$/', $token)) { return null; }
    return db()->one('SELECT * FROM download_tokens WHERE token_hash = ? AND expires_at > ?', [hash('sha256', $token), now()]);
}

/** Số lần còn được xin link tải mới (link gửi lúc đăng ký không tính). */
function download_links_left(int $userId): int
{
    $used = (int)db()->value('SELECT COUNT(*) FROM download_tokens WHERE user_id = ? AND created_at > ?', [$userId, ago((int)cfg('limits.download_hours', 24) * 3600)]);
    return max(0, (int)cfg('limits.download_links', 5) + download_register_bonus($userId) - $used);
}
function download_register_bonus(int $userId): int
{
    // Link tạo tự động lúc đăng ký nằm trong cùng khoảng thời gian thì không trừ vào số lượt của người dùng.
    $created = (string)db()->value('SELECT created_at FROM users WHERE id = ?', [$userId]);
    return $created > ago((int)cfg('limits.download_hours', 24) * 3600) ? 1 : 0;
}

/** Các file cài đặt đang có trên máy chủ: tên file => ['label' => ..., 'size' => byte]. */
function download_files(): array
{
    $dir = rtrim((string)cfg('downloads.dir'), '/\\');
    $out = [];
    foreach ((array)cfg('downloads.files', []) as $name => $label) {
        $path = $dir . DIRECTORY_SEPARATOR . $name;
        if (basename($name) === $name && is_file($path)) { $out[$name] = ['label' => $label, 'size' => filesize($path), 'path' => $path]; }
    }
    return $out;
}

function human_size(int $bytes): string
{
    return $bytes >= 1048576 ? number_format($bytes / 1048576, 1, ',', '.') . ' MB' : number_format(max(1, $bytes / 1024), 0, ',', '.') . ' KB';
}

// ---------------- Email ----------------
function mail_layout(string $title, string $bodyHtml): string
{
    $site = e((string)cfg('site.name', 'iTest'));
    $support = e((string)cfg('site.support', ''));
    return '<div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.6;color:#0F172A;max-width:600px;margin:0 auto;padding:24px">'
         . '<div style="font-size:22px;font-weight:800;color:#1D4ED8;margin-bottom:6px">' . $site . '</div>'
         . '<h2 style="font-size:19px;margin:0 0 14px">' . e($title) . '</h2>' . $bodyHtml
         . '<hr style="border:none;border-top:1px solid #E2E8F0;margin:24px 0 12px">'
         . '<p style="font-size:13px;color:#64748B;margin:0">Email này được gửi tự động từ trang web ' . $site . '.'
         . ($support !== '' ? ' Cần hỗ trợ, xin liên hệ ' . $support . '.' : '') . '</p></div>';
}

function mail_button(string $url, string $label): string
{
    return '<p style="margin:18px 0"><a href="' . e($url) . '" style="display:inline-block;background:#1D4ED8;color:#FFFFFF;text-decoration:none;font-weight:700;padding:12px 22px;border-radius:10px">'
         . e($label) . '</a></p><p style="font-size:13px;color:#64748B;word-break:break-all">Nếu nút không bấm được, hãy chép địa chỉ này vào trình duyệt:<br>' . e($url) . '</p>';
}

function mail_cert_note(array $cert): string
{
    $exp = $cert['expires'] ? 'có hiệu lực đến hết ngày ' . date('d/m/Y', strtotime($cert['expires'])) : 'không thời hạn';
    return '<p><b>Chứng chỉ sử dụng</b> được đính kèm email này (file <code>' . e($cert['name']) . '</code>, số ' . e($cert['id']) . ', ' . $exp . ').'
         . ' Khi cài iTest-Server và iTest-Editor, bộ cài sẽ yêu cầu chọn file này; tên Sở và tên trường trong phần mềm được lấy từ chứng chỉ.</p>'
         . '<p style="font-size:13px;color:#64748B">Chứng chỉ gắn với tên trường của quý thầy cô và được in trên đề thi, biên bản, bảng điểm. Vui lòng không chuyển cho đơn vị khác.</p>';
}

function mail_download_note(array $link): string
{
    return '<p><b>Link tải bộ cài</b> có hiệu lực trong ' . (int)$link['hours'] . ' giờ (đến ' . date('H:i', strtotime($link['expires'])) . ' ngày ' . date('d/m/Y', strtotime($link['expires'])) . '):</p>'
         . mail_button($link['url'], 'Tải bộ cài iTest')
         . '<p style="font-size:13px;color:#64748B">Hết hạn, thầy cô đăng nhập mục Tài khoản trên trang web để nhận link mới.</p>';
}

function send_welcome(array $user, array $cert, array $link): bool
{
    $html = mail_layout('Đăng ký thành công', '<p>Kính gửi thầy/cô ' . e($user['contact_name']) . ',</p>'
        . '<p>Tài khoản iTest của <b>' . e($user['school']) . '</b> (' . e($user['department']) . ') đã được tạo.</p>'
        . mail_download_note($link) . mail_cert_note($cert));
    return mailer()->send($user['email'], 'iTest – Link tải phần mềm và chứng chỉ sử dụng', $html,
        [['name' => $cert['name'], 'content' => $cert['content'], 'type' => 'application/json']]);
}

function send_certificate(array $user, array $cert, string $why): bool
{
    $html = mail_layout('Chứng chỉ sử dụng iTest', '<p>Kính gửi thầy/cô ' . e($user['contact_name']) . ',</p><p>' . e($why) . '</p>' . mail_cert_note($cert)
        . '<p>Trên máy đã cài phần mềm, mở iTest-Server và bấm <b>Đổi chứng chỉ…</b> để nạp file mới; iTest-Editor sẽ tự hỏi khi chứng chỉ cũ hết hạn.</p>');
    return mailer()->send($user['email'], 'iTest – Chứng chỉ sử dụng', $html, [['name' => $cert['name'], 'content' => $cert['content'], 'type' => 'application/json']]);
}

function send_download_link(array $user, array $link): bool
{
    $html = mail_layout('Link tải phần mềm iTest', '<p>Kính gửi thầy/cô ' . e($user['contact_name']) . ',</p>' . mail_download_note($link));
    return mailer()->send($user['email'], 'iTest – Link tải phần mềm', $html);
}

function send_password_reset(array $user, string $url): bool
{
    $html = mail_layout('Đặt lại mật khẩu', '<p>Kính gửi thầy/cô ' . e($user['contact_name']) . ',</p>'
        . '<p>Có yêu cầu đặt lại mật khẩu cho tài khoản iTest của thầy/cô. Link dưới đây có hiệu lực trong 1 giờ và chỉ dùng được một lần.</p>'
        . mail_button($url, 'Đặt lại mật khẩu') . '<p>Nếu thầy/cô không yêu cầu, xin bỏ qua email này; mật khẩu hiện tại vẫn giữ nguyên.</p>');
    return mailer()->send($user['email'], 'iTest – Đặt lại mật khẩu', $html);
}

function send_account_changed(array $user, string $what): bool
{
    $html = mail_layout('Thông tin tài khoản đã thay đổi', '<p>Kính gửi thầy/cô ' . e($user['contact_name']) . ',</p><p>' . e($what) . '</p>'
        . '<p>Nếu không phải thầy/cô thực hiện, hãy đặt lại mật khẩu ngay tại ' . e(site_url('forgot.php')) . '.</p>');
    return mailer()->send($user['email'], 'iTest – Thông tin tài khoản đã thay đổi', $html);
}
