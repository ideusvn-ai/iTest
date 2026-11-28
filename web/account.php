<?php
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/service.php';
require __DIR__ . '/lib/view.php';

$user = require_login();
$uid = (int)$user['id'];
$errors = [];

if (is_post()) {
    csrf_check();
    $action = field('action', 30);

    if ($action === 'profile') {
        // ---- Đổi thông tin ----
        $new = [
            'contact_name' => field('contact_name', 120),
            'phone'        => field('phone', 30),
            'department'   => field('department', 200),
            'school'       => field('school', 200),
        ];
        if (mb_strlen($new['contact_name']) < 3) { $errors[] = 'Vui lòng nhập họ tên người phụ trách.'; }
        if (!preg_match('/^[0-9+ .()-]{8,20}$/', $new['phone'])) { $errors[] = 'Số điện thoại không hợp lệ.'; }
        if (mb_strlen($new['department']) < 5) { $errors[] = 'Vui lòng nhập đầy đủ tên Sở / Phòng Giáo dục và Đào tạo.'; }
        if (mb_strlen($new['school']) < 5) { $errors[] = 'Vui lòng nhập đầy đủ tên trường.'; }
        // Đổi tên Sở hoặc tên trường làm chứng chỉ cũ không còn đúng: phải cấp chứng chỉ mới, tính vào số lượt lấy lại.
        $orgChanged = $new['department'] !== $user['department'] || $new['school'] !== $user['school'];
        if (!$errors && $orgChanged && cert_reissues_left($uid) < 1) {
            $errors[] = 'Đã hết lượt cấp lại chứng chỉ trong ' . (int)cfg('limits.cert_days', 30) . ' ngày nên chưa đổi được tên Sở / tên trường. Các thông tin khác vẫn đổi được.';
        }
        if (!$errors) {
            db()->exec('UPDATE users SET contact_name = ?, phone = ?, department = ?, school = ?, updated_at = ? WHERE id = ?',
                [$new['contact_name'], $new['phone'], $new['department'], $new['school'], now(), $uid]);
            $user = array_merge($user, $new);
            if ($orgChanged) {
                try {
                    $cert = cert_issue($user, 'profile');
                    $sent = send_certificate($user, $cert, 'Tên Sở / tên trường của tài khoản vừa được thay đổi, vì vậy chứng chỉ mới được cấp theo thông tin mới.');
                } catch (Throwable $e) { error_log('[iTest] ' . $e->getMessage()); $sent = false; }
                flash($sent ? 'ok' : 'warn', $sent
                    ? 'Đã lưu thông tin. Chứng chỉ mới theo tên Sở / tên trường mới đã được gửi tới ' . $user['email'] . '.'
                    : 'Đã lưu thông tin nhưng chưa gửi được chứng chỉ mới. Hãy bấm “Gửi lại chứng chỉ qua email”.');
            } else {
                flash('ok', 'Đã lưu thông tin.');
            }
            redirect('account.php');
        }
    } elseif ($action === 'password') {
        // ---- Đổi mật khẩu ----
        $old = is_string($_POST['old_password'] ?? null) ? $_POST['old_password'] : '';
        $pw = is_string($_POST['password'] ?? null) ? $_POST['password'] : '';
        $pw2 = is_string($_POST['password2'] ?? null) ? $_POST['password2'] : '';
        if (count_events('pw_fail', 900, (string)$uid) >= 5) { $errors[] = 'Nhập sai mật khẩu hiện tại quá nhiều lần. Vui lòng thử lại sau 15 phút.'; }
        elseif (!password_verify($old, $user['password_hash'])) { record_event('pw_fail', (string)$uid); $errors[] = 'Mật khẩu hiện tại không đúng.'; }
        elseif (($p = password_problem($pw, $pw2)) !== '') { $errors[] = $p; }
        if (!$errors) {
            db()->exec('UPDATE users SET password_hash = ?, updated_at = ? WHERE id = ?', [password_hash($pw, PASSWORD_DEFAULT), now(), $uid]);
            session_regenerate_id(true);
            send_account_changed($user, 'Mật khẩu tài khoản iTest của thầy/cô vừa được thay đổi lúc ' . date('H:i d/m/Y') . '.');
            flash('ok', 'Đã đổi mật khẩu.');
            redirect('account.php');
        }
    } elseif ($action === 'download') {
        // ---- Xin link tải mới (gửi qua email) ----
        if (download_links_left($uid) < 1) {
            flash('warn', 'Đã hết lượt xin link tải trong ' . (int)cfg('limits.download_hours', 24) . ' giờ. Link đã gửi trước đó vẫn dùng được tới khi hết hạn.');
        } else {
            $link = download_token_create($uid);
            $sent = send_download_link($user, $link);
            flash($sent ? 'ok' : 'error', $sent
                ? 'Link tải mới (hiệu lực ' . $link['hours'] . ' giờ) đã được gửi tới ' . $user['email'] . '.'
                : 'Chưa gửi được email. Vui lòng thử lại sau ít phút.');
        }
        redirect('account.php');
    } elseif ($action === 'certificate') {
        // ---- Lấy lại chứng chỉ (gửi qua email, có giới hạn lượt) ----
        if (cert_reissues_left($uid) < 1) {
            flash('warn', 'Đã hết lượt lấy lại chứng chỉ trong ' . (int)cfg('limits.cert_days', 30) . ' ngày.');
        } else {
            try {
                $cert = cert_issue($user, 'reissue');
                $sent = send_certificate($user, $cert, 'Chứng chỉ sử dụng được cấp lại theo yêu cầu từ trang Tài khoản.');
            } catch (Throwable $e) { error_log('[iTest] ' . $e->getMessage()); $sent = false; }
            flash($sent ? 'ok' : 'error', $sent ? 'Chứng chỉ đã được gửi tới ' . $user['email'] . '.' : 'Chưa gửi được chứng chỉ. Vui lòng thử lại sau ít phút.');
        }
        redirect('account.php');
    }
}

$cert = cert_latest($uid);
$certLeft = cert_reissues_left($uid);
$dlLeft = download_links_left($uid);
$files = download_files();
// Form thông tin: nếu vừa gửi mà có lỗi thì giữ lại những gì người dùng đã gõ.
$form = $user;
if (is_post() && $errors && field('action', 30) === 'profile') {
    foreach (['contact_name' => 120, 'phone' => 30, 'department' => 200, 'school' => 200] as $k => $max) { $form[$k] = field($k, $max); }
}

page_header('Tài khoản', 'account');
?>
<section class="account">
  <h1>Tài khoản của trường</h1>
  <p class="muted"><?= e($user['school']) ?> · <?= e($user['department']) ?> · <?= e($user['email']) ?></p>
  <?php if ($errors): ?><div class="flash flash-error"><ul><?php foreach ($errors as $m): ?><li><?= e($m) ?></li><?php endforeach; ?></ul></div><?php endif; ?>

  <div class="grid2">
    <div class="card">
      <h2>Tải xuống</h2>
      <p>Phiên bản hiện tại: <b><?= e((string)cfg('site.version', '')) ?></b>. Link tải được gửi qua email và có hiệu lực <?= (int)cfg('downloads.link_hours', 24) ?> giờ.</p>
      <?php if ($files): ?><ul class="files"><?php foreach ($files as $name => $f): ?><li><b><?= e($f['label']) ?></b><span><?= e($name) ?> · <?= e(human_size($f['size'])) ?></span></li><?php endforeach; ?></ul>
      <?php else: ?><p class="muted">Bộ cài đang được cập nhật, vui lòng quay lại sau.</p><?php endif; ?>
      <form method="post"><?= csrf_field() ?><input type="hidden" name="action" value="download">
        <button class="btn" type="submit" <?= $dlLeft < 1 || !$files ? 'disabled' : '' ?>>Gửi link tải qua email</button>
        <p class="muted small">Còn <?= $dlLeft ?> lượt trong <?= (int)cfg('limits.download_hours', 24) ?> giờ.</p>
      </form>
    </div>

    <div class="card">
      <h2>Chứng chỉ sử dụng</h2>
      <?php if ($cert): ?>
        <p>Số <b><?= e($cert['cert_id']) ?></b> · cấp ngày <?= e(date('d/m/Y', strtotime($cert['issued_on']))) ?> ·
           <?= $cert['expires_on'] ? 'hiệu lực đến hết ' . e(date('d/m/Y', strtotime($cert['expires_on']))) : 'không thời hạn' ?>.</p>
        <p class="muted small">Chứng chỉ ghi: <?= e($cert['school']) ?> · <?= e($cert['department']) ?>.</p>
      <?php else: ?><p class="muted">Chưa có chứng chỉ nào được cấp.</p><?php endif; ?>
      <p>iTest-Server và iTest-Editor yêu cầu file chứng chỉ khi cài đặt; tên Sở và tên trường trong phần mềm lấy từ chứng chỉ.</p>
      <form method="post"><?= csrf_field() ?><input type="hidden" name="action" value="certificate">
        <button class="btn" type="submit" <?= $certLeft < 1 ? 'disabled' : '' ?>>Gửi lại chứng chỉ qua email</button>
        <p class="muted small">Còn <?= $certLeft ?> lượt trong <?= (int)cfg('limits.cert_days', 30) ?> ngày. Mỗi lần lấy lại là một chứng chỉ mới với hạn dùng mới.</p>
      </form>
    </div>

    <div class="card">
      <h2>Thông tin</h2>
      <form method="post" novalidate><?= csrf_field() ?><input type="hidden" name="action" value="profile">
        <?= input('department', 'Sở / Phòng Giáo dục và Đào tạo', $form['department'], ['maxlength' => 200]) ?>
        <?= input('school', 'Tên trường', $form['school'], ['maxlength' => 200, 'hint' => 'Đổi tên Sở hoặc tên trường sẽ cấp chứng chỉ mới và tính một lượt lấy lại chứng chỉ.']) ?>
        <div class="row2">
          <?= input('contact_name', 'Người phụ trách', $form['contact_name'], ['maxlength' => 120]) ?>
          <?= input('phone', 'Số điện thoại', $form['phone'], ['maxlength' => 30, 'inputmode' => 'tel']) ?>
        </div>
        <label class="field"><span>Email</span><input type="email" value="<?= e($user['email']) ?>" disabled><small>Email đăng nhập không đổi được.</small></label>
        <button class="btn" type="submit">Lưu thông tin</button>
      </form>
    </div>

    <div class="card">
      <h2>Đổi mật khẩu</h2>
      <form method="post" novalidate><?= csrf_field() ?><input type="hidden" name="action" value="password">
        <?= input('old_password', 'Mật khẩu hiện tại', '', ['type' => 'password', 'autocomplete' => 'current-password']) ?>
        <?= input('password', 'Mật khẩu mới', '', ['type' => 'password', 'minlength' => 8, 'autocomplete' => 'new-password', 'hint' => 'Ít nhất 8 ký tự.']) ?>
        <?= input('password2', 'Nhập lại mật khẩu mới', '', ['type' => 'password', 'minlength' => 8, 'autocomplete' => 'new-password']) ?>
        <button class="btn" type="submit">Đổi mật khẩu</button>
      </form>
    </div>
  </div>
</section>
<?php page_footer();
