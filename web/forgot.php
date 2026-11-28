<?php
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/service.php';
require __DIR__ . '/lib/view.php';

$done = false;
if (is_post()) {
    csrf_check();
    $email = mb_strtolower(field('email', 190));
    // Luôn trả lời như nhau để không tiết lộ email nào có tài khoản; mỗi email và mỗi địa chỉ IP bị giới hạn số lần yêu cầu.
    if (count_events('forgot', 3600, $email) < 3 && count_events('forgot', 3600, null, client_ip()) < 10) {
        record_event('forgot', $email);
        $user = db()->one('SELECT * FROM users WHERE email = ?', [$email]);
        if ($user) {
            $token = bin2hex(random_bytes(24));
            db()->exec('INSERT INTO password_resets (user_id, token_hash, expires_at, created_at) VALUES (?, ?, ?, ?)',
                [(int)$user['id'], hash('sha256', $token), date('Y-m-d H:i:s', time() + 3600), now()]);
            send_password_reset($user, site_url('reset.php?t=' . $token));
        }
    }
    $done = true;
}

page_header('Quên mật khẩu');
?>
<section class="card form-card narrow">
  <h1>Quên mật khẩu</h1>
  <?php if ($done): ?>
    <div class="flash flash-ok">Nếu email này có tài khoản, hướng dẫn đặt lại mật khẩu đã được gửi đi. Link có hiệu lực trong 1 giờ.</div>
    <p><a href="login.php">Quay lại đăng nhập</a></p>
  <?php else: ?>
    <form method="post" novalidate><?= csrf_field() ?>
      <?= input('email', 'Email đã đăng ký', '', ['type' => 'email', 'maxlength' => 190, 'autocomplete' => 'email']) ?>
      <button class="btn" type="submit">Gửi hướng dẫn đặt lại mật khẩu</button>
    </form>
  <?php endif; ?>
</section>
<?php page_footer();
