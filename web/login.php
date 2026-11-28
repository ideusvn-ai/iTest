<?php
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/view.php';

if (current_user()) { redirect('account.php'); }

$email = '';
$error = '';
if (is_post()) {
    csrf_check();
    $email = mb_strtolower(field('email', 190));
    $pw = is_string($_POST['password'] ?? null) ? $_POST['password'] : '';
    $max = (int)cfg('limits.login_failures', 8);
    if (count_events('login_fail', 900, $email) >= $max || count_events('login_fail', 900, null, client_ip()) >= $max * 3) {
        $error = 'Đăng nhập sai quá nhiều lần. Vui lòng thử lại sau 15 phút, hoặc dùng “Quên mật khẩu”.';
    } else {
        $user = db()->one('SELECT id, password_hash FROM users WHERE email = ?', [$email]);
        // Luôn chạy password_verify (kể cả khi email không tồn tại) để thời gian trả lời không tiết lộ email nào có tài khoản.
        $hash = $user['password_hash'] ?? '$2y$10$usesomesillystringfore7hnbRJHxXVLeakoG8K30oukPsA.ztMG';
        if (password_verify($pw, $hash) && $user) {
            if (password_needs_rehash($user['password_hash'], PASSWORD_DEFAULT)) {
                db()->exec('UPDATE users SET password_hash = ? WHERE id = ?', [password_hash($pw, PASSWORD_DEFAULT), (int)$user['id']]);
            }
            login_user((int)$user['id']);
            redirect('account.php');
        }
        record_event('login_fail', $email);
        $error = 'Email hoặc mật khẩu không đúng.';
    }
}

page_header('Đăng nhập');
?>
<section class="card form-card narrow">
  <h1>Đăng nhập</h1>
  <?php if ($error): ?><div class="flash flash-error"><?= e($error) ?></div><?php endif; ?>
  <form method="post" novalidate>
    <?= csrf_field() ?>
    <?= input('email', 'Email', $email, ['type' => 'email', 'maxlength' => 190, 'autocomplete' => 'email']) ?>
    <?= input('password', 'Mật khẩu', '', ['type' => 'password', 'autocomplete' => 'current-password']) ?>
    <button class="btn" type="submit">Đăng nhập</button>
    <p class="muted small"><a href="forgot.php">Quên mật khẩu?</a> · Chưa có tài khoản? <a href="register.php">Đăng ký cho trường</a></p>
  </form>
</section>
<?php page_footer();
