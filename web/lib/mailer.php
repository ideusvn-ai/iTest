<?php
// Gửi email có file đính kèm, không cần thư viện ngoài: qua SMTP (khuyên dùng) hoặc hàm mail() của máy chủ.

final class Mailer
{
    public string $lastError = '';

    public function __construct(private array $cfg) {}

    /**
     * @param array $attachments Danh sách ['name' => tên file, 'content' => nội dung, 'type' => kiểu MIME]
     * @return bool true nếu máy chủ thư đã nhận thư
     */
    public function send(string $to, string $subject, string $html, array $attachments = []): bool
    {
        $this->lastError = '';
        if (!filter_var($to, FILTER_VALIDATE_EMAIL)) { $this->lastError = 'Địa chỉ email không hợp lệ.'; return false; }
        $from = $this->cfg['from'] ?? '';
        $fromName = $this->cfg['from_name'] ?? 'iTest';
        [$headers, $body] = $this->build($from, $fromName, $to, $subject, $html, $attachments);
        try {
            switch ($this->cfg['driver'] ?? 'smtp') {
                case 'file':   // chỉ dùng khi chạy thử: ghi thư ra thư mục
                    $dir = $this->cfg['dir'];
                    if (!is_dir($dir)) { mkdir($dir, 0700, true); }
                    $name = sprintf('%s/%s-%s.eml', $dir, date('Ymd-His'), bin2hex(random_bytes(4)));
                    return file_put_contents($name, "To: $to\r\nSubject: " . $this->encodeHeader($subject) . "\r\n" . $headers . "\r\n" . $body) !== false;
                case 'mail':
                    return mail($to, $this->encodeHeader($subject), $body, $headers, '-f' . $from);
                default:
                    $this->smtp($from, $to, "To: <$to>\r\nSubject: " . $this->encodeHeader($subject) . "\r\n" . $headers . "\r\n" . $body);
                    return true;
            }
        } catch (Throwable $e) {
            $this->lastError = $e->getMessage();
            error_log('[iTest] Gửi mail lỗi: ' . $e->getMessage());
            return false;
        }
    }

    private function encodeHeader(string $s): string
    {
        return preg_match('/[^\x20-\x7E]/', $s) ? '=?UTF-8?B?' . base64_encode($s) . '?=' : $s;
    }

    /** Dựng phần đầu thư (không gồm To/Subject) và thân thư MIME. */
    private function build(string $from, string $fromName, string $to, string $subject, string $html, array $attachments): array
    {
        $host = substr(strrchr($from, '@') ?: '@localhost', 1);
        $headers = 'From: ' . $this->encodeHeader($fromName) . " <$from>\r\n"
                 . 'Date: ' . date('r') . "\r\n"
                 . 'Message-ID: <' . bin2hex(random_bytes(12)) . "@$host>\r\n"
                 . "MIME-Version: 1.0\r\n";
        $text = trim(html_entity_decode(strip_tags(preg_replace('~<(br|/p|/h\d|/li|/tr)[^>]*>~i', "\n", $html)), ENT_QUOTES, 'UTF-8'));
        $alt = 'alt_' . bin2hex(random_bytes(8));
        $content = "--$alt\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" . chunk_split(base64_encode($text))
                 . "--$alt\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" . chunk_split(base64_encode($html))
                 . "--$alt--\r\n";
        if (!$attachments) {
            return [$headers . "Content-Type: multipart/alternative; boundary=\"$alt\"\r\n", $content];
        }
        $mix = 'mix_' . bin2hex(random_bytes(8));
        $body = "--$mix\r\nContent-Type: multipart/alternative; boundary=\"$alt\"\r\n\r\n" . $content;
        foreach ($attachments as $a) {
            $name = preg_replace('/[^A-Za-z0-9._-]/', '_', $a['name']);
            $body .= "--$mix\r\nContent-Type: " . ($a['type'] ?? 'application/octet-stream') . "; name=\"$name\"\r\n"
                   . "Content-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"$name\"\r\n\r\n"
                   . chunk_split(base64_encode($a['content']));
        }
        $body .= "--$mix--\r\n";
        return [$headers . "Content-Type: multipart/mixed; boundary=\"$mix\"\r\n", $body];
    }

    private function smtp(string $from, string $to, string $message): void
    {
        $host = $this->cfg['host'] ?? '';
        $port = (int)($this->cfg['port'] ?? 587);
        $secure = strtolower((string)($this->cfg['secure'] ?? 'tls'));
        if ($host === '') { throw new RuntimeException('Chưa cấu hình máy chủ SMTP.'); }
        $fp = @stream_socket_client(($secure === 'ssl' ? 'ssl://' : 'tcp://') . $host . ':' . $port, $errno, $errstr, 15);
        if (!$fp) { throw new RuntimeException("Không kết nối được máy chủ thư: $errstr"); }
        stream_set_timeout($fp, 20);
        try {
            $this->expect($fp, 220);
            $me = preg_replace('/[^A-Za-z0-9.-]/', '', $_SERVER['SERVER_NAME'] ?? 'localhost') ?: 'localhost';
            $this->cmd($fp, "EHLO $me", 250);
            if ($secure === 'tls') {
                $this->cmd($fp, 'STARTTLS', 220);
                if (!stream_socket_enable_crypto($fp, true, STREAM_CRYPTO_METHOD_TLS_CLIENT)) { throw new RuntimeException('Không bật được mã hoá TLS với máy chủ thư.'); }
                $this->cmd($fp, "EHLO $me", 250);
            }
            if (($this->cfg['user'] ?? '') !== '') {
                $this->cmd($fp, 'AUTH LOGIN', 334);
                $this->cmd($fp, base64_encode($this->cfg['user']), 334);
                $this->cmd($fp, base64_encode($this->cfg['pass'] ?? ''), 235);
            }
            $this->cmd($fp, "MAIL FROM:<$from>", 250);
            $this->cmd($fp, "RCPT TO:<$to>", 250);
            $this->cmd($fp, 'DATA', 354);
            // Dòng bắt đầu bằng dấu chấm phải được nhân đôi dấu chấm (RFC 5321).
            $data = preg_replace('/^\./m', '..', str_replace(["\r\n", "\r"], "\n", $message));
            fwrite($fp, str_replace("\n", "\r\n", $data) . "\r\n.\r\n");
            $this->expect($fp, 250);
            fwrite($fp, "QUIT\r\n");
        } finally {
            fclose($fp);
        }
    }

    private function cmd($fp, string $line, int $code): void
    {
        fwrite($fp, $line . "\r\n");
        $this->expect($fp, $code);
    }

    private function expect($fp, int $code): void
    {
        $reply = '';
        while (($line = fgets($fp, 1024)) !== false) {
            $reply .= $line;
            if (strlen($line) < 4 || $line[3] !== '-') { break; }   // dòng cuối của câu trả lời có dấu cách sau mã
        }
        if ((int)substr($reply, 0, 3) !== $code) {
            throw new RuntimeException('Máy chủ thư trả lời: ' . trim($reply ?: '(không có phản hồi)'));
        }
    }
}
