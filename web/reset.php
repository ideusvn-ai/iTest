<?php
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/service.php';
require __DIR__ . '/lib/view.php';

$token = is_string($_REQUEST['t'] ?? null) ? $_REQUEST['t'] : '';
$row = preg_match('/^[a-f0-9]{48}$/', $token)
    ? db()->one('SELECT * FROM password_resets WHERE token_hash = ? AND used_at IS NULL AND expires_at > ?', [hash('sha256', $token), now()])
    : null;
$error = '';

if ($row && is_post()) {
    csrf_check();
    $pw = is_string($_POST['password'] ?? null) ? $_POST['password'] : '';
    $pw2 = is_string($_POST['password2'] ?? null) ? $_POST['password2'] : '';
    $error = password_problem($pw, $pw2);
    if ($error === '') {
        // Đánh dấu đã dùng trước, và chỉ đổi mật khẩu nếu chính yêu cầu này đánh dấu được (link chỉ dùng một lần).
        if (db()->exec('UPDATE password_resets SET used_at = ? WHERE id = ? AND used_at IS NULL', [now(), (int)$row['id']]) === 1) {
            db()->exec('UPDATE users SET password_hash = ?, updated_at = ? WHERE id = ?', [password_hash($pw, PASSWORD_DEFAULT), now(), (int)$row['user_id']]);
            db()->exec('UPDATE password_resets SET used_at = ? WHERE user_id = ? AND used_at IS NULL', [now(), (int)$row['user_id']]);
            flash('ok', 'Đã đặt mật khẩu mới. Mời thầy cô đăng nhập.');
        }
        redirect('login.php');
    }
}

page_header('Đặt lại mật khẩu');
?>
<section class="card form-card narrow">
  <h1>Đặt lại mật khẩu</h1>
  <?php if (!$row): ?>
    <p>Link đặt lại mật khẩu đã hết hạn hoặc đã được dùng.</p>
    <p><a class="btn" href="forgot.php">Yêu cầu link mới</a></p>
  <?php else: ?>
    <?php if ($error): ?><div class="flash flash-error"><?= e($error) ?></div><?php endif; ?>
    <form method="post" novalidate><?= csrf_field() ?><input type="hidden" name="t" value="<?= e($token) ?>">
      <?= input('password', 'Mật khẩu mới', '', ['type' => 'password', 'minlength' => 8, 'autocomplete' => 'new-password', 'hint' => 'Ít nhất 8 ký tự.']) ?>
      <?= input('password2', 'Nhập lại mật khẩu mới', '', ['type' => 'password', 'minlength' => 8, 'autocomplete' => 'new-password']) ?>
      <button class="btn" type="submit">Đặt mật khẩu mới</button>
    </form>
  <?php endif; ?>
</section>
<?php page_footer();
