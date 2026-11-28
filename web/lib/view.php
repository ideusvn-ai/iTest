<?php
// Khung trang chung: phần đầu, thanh điều hướng, thông báo và chân trang.
declare(strict_types=1);

function page_header(string $title, string $active = ''): void
{
    $user = current_user();
    $site = e((string)cfg('site.name', 'iTest'));
    ?><!DOCTYPE html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title><?= e($title) ?> · <?= $site ?></title>
<meta name="description" content="iTest – bộ phần mềm thi trên máy tính trong mạng LAN cho trường học: soạn đề, mở phòng thi, giám sát và xuất kết quả.">
<link rel="icon" href="assets/icon.png">
<link rel="stylesheet" href="assets/site.css?v=3">
</head>
<body>
<header class="nav">
  <a class="brand" href="index.php"><img src="assets/icon.png" alt="" width="34" height="34"><span><?= $site ?></span></a>
  <nav class="links" aria-label="Điều hướng">
    <a href="index.php#giao-dien">Giao diện</a>
    <a href="index.php#tinh-nang">Tính năng</a>
    <a href="index.php#dang-ky">Cách nhận phần mềm</a>
    <a href="index.php#hoi-dap">Hỏi đáp</a>
  </nav>
  <div class="nav-actions">
  <?php if ($user): ?>
    <a class="btn btn-sm <?= $active === 'account' ? '' : 'btn-ghost' ?>" href="account.php">Tài khoản</a>
    <form method="post" action="logout.php" class="inline"><?= csrf_field() ?><button class="btn btn-sm btn-ghost" type="submit">Đăng xuất</button></form>
  <?php else: ?>
    <a class="btn btn-sm btn-ghost" href="login.php">Đăng nhập</a>
    <a class="btn btn-sm" href="register.php">Đăng ký cho trường</a>
  <?php endif; ?>
  </div>
</header>
<main>
<?php foreach (take_flash() as [$type, $message]): ?>
  <div class="flash flash-<?= e($type) ?>" role="status"><?= e($message) ?></div>
<?php endforeach;
}

function page_footer(): void
{
    $support = (string)cfg('site.support', '');
    ?>
</main>
<footer class="foot">
  <div><b><?= e((string)cfg('site.name', 'iTest')) ?></b> · Phần mềm thi trên máy tính trong mạng LAN dành cho trường học.</div>
  <div><?php if ($support !== ''): ?>Hỗ trợ: <a href="mailto:<?= e($support) ?>"><?= e($support) ?></a> · <?php endif; ?>Phiên bản <?= e((string)cfg('site.version', '')) ?></div>
</footer>
<script src="assets/site.js?v=3" defer></script>
</body>
</html>
<?php
}

/** Một ô nhập có nhãn, giữ lại giá trị đã nhập và hiện lỗi nếu có. */
function input(string $name, string $label, string $value = '', array $opt = []): string
{
    $type = $opt['type'] ?? 'text';
    $attrs = '';
    foreach (['maxlength', 'autocomplete', 'placeholder', 'inputmode', 'minlength'] as $k) {
        if (isset($opt[$k])) { $attrs .= ' ' . $k . '="' . e((string)$opt[$k]) . '"'; }
    }
    if (($opt['required'] ?? true) === true) { $attrs .= ' required'; }
    $hint = isset($opt['hint']) ? '<small>' . e($opt['hint']) . '</small>' : '';
    return '<label class="field"><span>' . e($label) . '</span><input type="' . e($type) . '" name="' . e($name) . '" value="' . e($type === 'password' ? '' : $value) . '"' . $attrs . '>' . $hint . '</label>';
}
