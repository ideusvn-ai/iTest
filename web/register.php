<?php
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/service.php';
require __DIR__ . '/lib/view.php';

if (current_user()) { redirect('account.php'); }

$v = ['email' => '', 'contact_name' => '', 'phone' => '', 'department' => '', 'school' => ''];
$errors = [];

if (is_post()) {
    csrf_check();
    $v['email'] = mb_strtolower(field('email', 190));
    $v['contact_name'] = field('contact_name', 120);
    $v['phone'] = field('phone', 30);
    $v['department'] = field('department', 200);
    $v['school'] = field('school', 200);
    $pw = is_string($_POST['password'] ?? null) ? $_POST['password'] : '';
    $pw2 = is_string($_POST['password2'] ?? null) ? $_POST['password2'] : '';

    if (!filter_var($v['email'], FILTER_VALIDATE_EMAIL)) { $errors[] = 'Email không hợp lệ.'; }
    if (mb_strlen($v['contact_name']) < 3) { $errors[] = 'Vui lòng nhập họ tên người phụ trách.'; }
    if (!preg_match('/^[0-9+ .()-]{8,20}$/', $v['phone'])) { $errors[] = 'Số điện thoại không hợp lệ.'; }
    if (mb_strlen($v['department']) < 5) { $errors[] = 'Vui lòng nhập đầy đủ tên Sở / Phòng Giáo dục và Đào tạo.'; }
    if (mb_strlen($v['school']) < 5) { $errors[] = 'Vui lòng nhập đầy đủ tên trường.'; }
    if (($p = password_problem($pw, $pw2)) !== '') { $errors[] = $p; }
    if (empty($_POST['agree'])) { $errors[] = 'Vui lòng xác nhận thông tin đăng ký là chính xác.'; }
    if (!$errors && count_events('register', 3600, null, client_ip()) >= (int)cfg('limits.registrations', 5)) {
        $errors[] = 'Có quá nhiều lượt đăng ký từ mạng này. Vui lòng thử lại sau một giờ.';
    }
    if (!$errors && db()->value('SELECT id FROM users WHERE email = ?', [$v['email']]) !== null) {
        $errors[] = 'Email này đã có tài khoản. Hãy đăng nhập, hoặc dùng “Quên mật khẩu”.';
    }

    if (!$errors) {
        try {
            $id = db()->insert('INSERT INTO users (email, password_hash, contact_name, phone, department, school, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
                [$v['email'], password_hash($pw, PASSWORD_DEFAULT), $v['contact_name'], $v['phone'], $v['department'], $v['school'], now(), now()]);
        } catch (Throwable $e) {
            error_log('[iTest] Đăng ký lỗi: ' . $e->getMessage());
            $id = 0;
            $errors[] = 'Không tạo được tài khoản (email có thể vừa được đăng ký). Vui lòng thử lại.';
        }
        if ($id > 0) {
            record_event('register', $v['email']);
            $user = db()->one('SELECT * FROM users WHERE id = ?', [$id]);
            login_user($id);
            // Chứng chỉ và link tải chỉ được gửi qua email đã đăng ký: email sai thì không nhận được gì.
            try {
                $cert = cert_issue($user, 'register');
                $link = download_token_create($id);
                $sent = send_welcome($user, $cert, $link);
            } catch (Throwable $e) {
                error_log('[iTest] Cấp chứng chỉ lỗi: ' . $e->getMessage());
                $sent = false;
            }
            if ($sent) {
                flash('ok', 'Đăng ký thành công. Link tải phần mềm (hiệu lực 24 giờ) và chứng chỉ sử dụng đã được gửi tới ' . $v['email'] . '. Nếu chưa thấy, hãy kiểm tra hộp thư rác.');
            } else {
                flash('warn', 'Tài khoản đã được tạo nhưng chưa gửi được email. Hãy dùng các nút “Gửi qua email” trong trang Tài khoản để nhận link tải và chứng chỉ.');
            }
            redirect('account.php');
        }
    }
}

page_header('Đăng ký cho trường');
?>
<section class="card form-card">
  <h1>Đăng ký sử dụng iTest cho trường</h1>
  <p class="muted">Sau khi đăng ký, link tải phần mềm (hiệu lực 24 giờ) và <b>chứng chỉ sử dụng</b> ghi tên Sở, tên trường sẽ được gửi vào email của thầy cô.
     Tên Sở và tên trường trong phần mềm lấy từ chứng chỉ, vì vậy xin nhập chính xác, đầy đủ, có dấu.</p>
  <?php if ($errors): ?><div class="flash flash-error"><ul><?php foreach ($errors as $m): ?><li><?= e($m) ?></li><?php endforeach; ?></ul></div><?php endif; ?>
  <form method="post" novalidate>
    <?= csrf_field() ?>
    <h2>Đơn vị</h2>
    <?= input('department', 'Sở / Phòng Giáo dục và Đào tạo', $v['department'], ['maxlength' => 200, 'placeholder' => 'Sở Giáo dục và Đào tạo …', 'hint' => 'In ở dòng trên cùng của đề thi.']) ?>
    <?= input('school', 'Tên trường', $v['school'], ['maxlength' => 200, 'placeholder' => 'Trường THPT …', 'hint' => 'In trên đề thi, biên bản phòng thi và bảng điểm.']) ?>
    <h2>Người phụ trách</h2>
    <div class="row2">
      <?= input('contact_name', 'Họ và tên', $v['contact_name'], ['maxlength' => 120, 'autocomplete' => 'name']) ?>
      <?= input('phone', 'Số điện thoại', $v['phone'], ['maxlength' => 30, 'inputmode' => 'tel', 'autocomplete' => 'tel']) ?>
    </div>
    <?= input('email', 'Email', $v['email'], ['type' => 'email', 'maxlength' => 190, 'autocomplete' => 'email', 'hint' => 'Link tải và chứng chỉ chỉ được gửi tới email này.']) ?>
    <div class="row2">
      <?= input('password', 'Mật khẩu', '', ['type' => 'password', 'minlength' => 8, 'autocomplete' => 'new-password', 'hint' => 'Ít nhất 8 ký tự.']) ?>
      <?= input('password2', 'Nhập lại mật khẩu', '', ['type' => 'password', 'minlength' => 8, 'autocomplete' => 'new-password']) ?>
    </div>
    <label class="check"><input type="checkbox" name="agree" value="1" required> <span>Tôi xác nhận thông tin trên là chính xác và tôi đăng ký thay mặt cho trường nêu trên.</span></label>
    <button class="btn" type="submit">Đăng ký và nhận phần mềm qua email</button>
    <p class="muted small">Đã có tài khoản? <a href="login.php">Đăng nhập</a></p>
  </form>
</section>
<?php page_footer();
