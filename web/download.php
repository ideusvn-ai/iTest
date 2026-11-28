<?php
// Trang tải xuống theo link có thời hạn (gửi qua email). Không cần đăng nhập: ai giữ link còn hạn thì tải được,
// để thầy cô có thể chuyển link cho người phụ trách phòng máy của trường.
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/service.php';
require __DIR__ . '/lib/view.php';

$token = is_string($_GET['t'] ?? null) ? $_GET['t'] : '';
$row = download_token_find($token);
$files = download_files();

if ($row && isset($_GET['f'])) {
    $name = is_string($_GET['f']) ? $_GET['f'] : '';
    if (!isset($files[$name])) { http_response_code(404); exit('Không tìm thấy file.'); }
    db()->exec('UPDATE download_tokens SET hits = hits + 1 WHERE id = ?', [(int)$row['id']]);
    session_write_close();
    while (ob_get_level() > 0) { ob_end_clean(); }
    set_time_limit(0);
    header('Content-Type: application/octet-stream');
    header('Content-Disposition: attachment; filename="' . $name . '"');
    header('Content-Length: ' . $files[$name]['size']);
    header('Cache-Control: private, no-store');
    readfile($files[$name]['path']);
    exit;
}

page_header('Tải xuống');
if (!$row): http_response_code(410); ?>
<section class="card form-card narrow">
  <h1>Link tải đã hết hạn</h1>
  <p>Link tải chỉ có hiệu lực trong <?= (int)cfg('downloads.link_hours', 24) ?> giờ kể từ khi gửi, hoặc link không đúng.</p>
  <p><a class="btn" href="account.php">Đăng nhập để nhận link mới</a></p>
</section>
<?php else:
    $owner = db()->one('SELECT school, department FROM users WHERE id = ?', [(int)$row['user_id']]); ?>
<section class="card form-card">
  <h1>Tải bộ cài iTest</h1>
  <p class="muted">Dành cho <?= e($owner['school'] ?? '') ?> · link có hiệu lực đến <?= e(date('H:i', strtotime($row['expires_at']))) ?> ngày <?= e(date('d/m/Y', strtotime($row['expires_at']))) ?>.</p>
  <?php if (!$files): ?><p>Bộ cài đang được cập nhật, vui lòng quay lại sau.</p><?php endif; ?>
  <ul class="files files-dl">
  <?php foreach ($files as $name => $f): ?>
    <li><div><b><?= e($f['label']) ?></b><span><?= e($name) ?> · <?= e(human_size($f['size'])) ?></span></div>
        <a class="btn btn-sm" href="download.php?t=<?= e($token) ?>&amp;f=<?= e(rawurlencode($name)) ?>">Tải về</a></li>
  <?php endforeach; ?>
  </ul>
  <h2>Cài đặt</h2>
  <ol class="steps">
    <li>Cài <b>iTest-Server</b> trên máy giám thị và <b>iTest-Editor</b> trên máy giáo viên soạn đề. Bộ cài sẽ yêu cầu chọn file <b>chứng chỉ (.itcert)</b> đính kèm email đăng ký.</li>
    <li>Cài <b>iTest</b> trên các máy thí sinh (không cần chứng chỉ).</li>
    <li>Cả ba ứng dụng phải cùng phiên bản. Bộ cài tự gỡ bản cũ trước khi cài bản mới; bộ đề và kết quả thi không bị xoá.</li>
  </ol>
</section>
<?php endif;
page_footer();
