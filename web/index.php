<?php
require __DIR__ . '/lib/app.php';
require __DIR__ . '/lib/view.php';

/** Nếu có ảnh động thật (GIF/WebP) trong assets/screens thì dùng ảnh đó thay cho hình minh hoạ dựng bằng CSS. */
function screen_image(string $name): ?string
{
    foreach (['webp', 'gif', 'png'] as $ext) {
        if (is_file(__DIR__ . "/assets/screens/$name.$ext")) { return "assets/screens/$name.$ext"; }
    }
    return null;
}
$logged = current_user() !== null;
$cta = $logged ? 'account.php' : 'register.php';

page_header('Thi trên máy tính trong mạng LAN');
?>
<section class="hero">
  <div class="hero-text">
    <span class="pill"><i class="dot"></i>Phiên bản <?= e((string)cfg('site.version', '')) ?> · Windows 10/11 · dành cho trường học</span>
    <h1>Phòng thi trên máy tính,<br><span class="grad">gọn trong một mạng LAN.</span></h1>
    <p class="lead">iTest là bộ ba phần mềm cho phòng máy của trường: soạn đề, mở phòng thi, giám sát thí sinh và xuất kết quả. Không cần Internet trong giờ thi, không cần máy chủ đám mây.</p>
    <div class="cta">
      <a class="btn" href="<?= $cta ?>"><?= $logged ? 'Vào trang tài khoản' : 'Đăng ký cho trường và tải về' ?></a>
      <a class="btn btn-ghost" href="#giao-dien">Xem giao diện</a>
    </div>
    <ul class="checks"><li>Khoá máy khi thi</li><li>Đề mã hoá, đường truyền mã hoá</li><li>Tự lưu, khôi phục khi sự cố</li></ul>
  </div>
  <div class="hero-art" aria-hidden="true">
    <div class="lan">
      <div class="lan-server"><span class="ring"></span><span class="ring r2"></span><b>iTest-Server</b><small>Máy giám thị</small></div>
      <div class="lan-pc p1"><i class="st ok"></i>PC-01</div>
      <div class="lan-pc p2"><i class="st ok"></i>PC-02</div>
      <div class="lan-pc p3"><i class="st done"></i>PC-03</div>
      <div class="lan-pc p4"><i class="st ok"></i>PC-04</div>
      <div class="lan-pc p5"><i class="st warn"></i>PC-05</div>
      <div class="lan-pc p6"><i class="st ok"></i>PC-06</div>
      <div class="toast t1"><b>PC-03</b> đã nộp bài · 36/40</div>
      <div class="toast t2"><b>PC-05</b> mất kết nối → tự nối lại</div>
    </div>
  </div>
</section>

<section class="band" aria-label="Điểm nổi bật">
  <div><b>3</b><span>ứng dụng trong một bộ</span></div>
  <div><b>LAN</b><span>chạy hoàn toàn nội bộ</span></div>
  <div><b>6</b><span>loại câu hỏi, có công thức toán</span></div>
  <div><b>Word · Excel</b><span>nhập câu hỏi có sẵn</span></div>
</section>

<section id="giao-dien" class="sec">
  <div class="head"><span class="kicker">Giao diện</span><h2>Ba ứng dụng, ba vai trò</h2>
    <p>Hình động dưới đây minh hoạ thao tác chính của từng ứng dụng.</p></div>
  <div class="demo" data-demo>
    <div class="demo-tabs" role="tablist">
      <button class="on" role="tab" data-tab="client"><b>iTest</b><span>Máy thí sinh</span></button>
      <button role="tab" data-tab="server"><b>iTest-Server</b><span>Máy giám thị</span></button>
      <button role="tab" data-tab="editor"><b>iTest-Editor</b><span>Máy giáo viên soạn đề</span></button>
    </div>
    <div class="demo-stage">

      <!-- ============ iTest: màn hình làm bài ============ -->
      <div class="screen on" data-screen="client">
        <?php if ($img = screen_image('itest')): ?><img src="<?= e($img) ?>" alt="Màn hình làm bài của iTest" loading="lazy"><?php else: ?>
        <div class="win">
          <div class="win-bar"><span class="win-title">Kiểm tra giữa kỳ I – Toán 12 · Mã đề 102</span><span class="clock" data-clock="2315">38:35</span><span class="chip">Nộp bài</span></div>
          <div class="win-body split">
            <div class="paper">
              <div class="paper-head"><div>SỞ GIÁO DỤC VÀ ĐÀO TẠO …<br><b>TRƯỜNG THPT …</b></div><div class="r"><b>ĐỀ KIỂM TRA GIỮA KỲ I</b><br>Môn: Toán · 45 phút</div></div>
              <p class="q"><b>Câu 12.</b> Hàm số <i>y = x³ − 3x + 1</i> đạt cực đại tại điểm</p>
              <div class="opts"><span class="opt o1">A. x = 1</span><span class="opt o2 pick">B. x = −1</span><span class="opt o3">C. x = 0</span><span class="opt o4">D. x = 3</span></div>
              <p class="q"><b>Câu 13.</b> Mỗi mệnh đề sau đúng hay sai?</p>
              <div class="tf"><span>a) Hàm số đồng biến trên (1; +∞)</span><b class="tfp t">Đ</b></div>
              <div class="tf"><span>b) Đồ thị cắt trục tung tại (0; 2)</span><b class="tfp f">S</b></div>
            </div>
            <div class="sheet">
              <b>Phiếu trả lời</b>
              <div class="bubbles"><?php for ($i = 1; $i <= 24; $i++): ?><i class="<?= $i < 12 ? 'f' : ($i === 12 ? 'f now' : '') ?>"><?= $i ?></i><?php endfor; ?></div>
              <div class="prog"><span></span></div><small>Đã làm 12/40 · tự lưu mỗi giây</small>
            </div>
          </div>
        </div><?php endif; ?>
        <ul class="demo-notes"><li>Đề trình bày như giấy thi, phiếu tô đáp án bên cạnh</li><li>Toàn màn hình, khoá Alt+Tab và phím Windows trong giờ thi</li><li>Mất mạng hay mất điện, bài làm vẫn còn và tự gửi lại</li></ul>
      </div>

      <!-- ============ iTest-Server: bảng điều khiển phòng thi ============ -->
      <div class="screen" data-screen="server">
        <?php if ($img = screen_image('itest-server')): ?><img src="<?= e($img) ?>" alt="Bảng điều khiển phòng thi của iTest-Server" loading="lazy"><?php else: ?>
        <div class="win">
          <div class="win-bar"><span class="win-title">Trường THPT … · Phòng máy 1 · Mã phòng 4827</span><span class="clock" data-clock="2315">38:35</span><span class="chip warn">Tạm dừng</span></div>
          <div class="win-body split srv">
            <div class="table">
              <div class="tr th"><span>SBD</span><span>Họ và tên</span><span>Máy</span><span>Trạng thái</span></div>
              <div class="tr"><span>001</span><span>Nguyễn Văn An</span><span>PC-01</span><span><i class="st ok"></i>Đang làm bài (<em class="cnt" data-count="30">30</em>/40)</span></div>
              <div class="tr"><span>002</span><span>Trần Thị Bình</span><span>PC-02</span><span><i class="st ok"></i>Đang làm bài (<em class="cnt" data-count="27">27</em>/40)</span></div>
              <div class="tr flip"><span>003</span><span>Lê Văn Cường</span><span>PC-03</span><span class="a"><i class="st ok"></i>Đang làm bài (36/40)</span><span class="b"><i class="st done"></i>Đã nộp bài (36/40)</span></div>
              <div class="tr flip2"><span>004</span><span>Phạm Thị Dung</span><span>PC-05</span><span class="a"><i class="st warn"></i>Mất kết nối</span><span class="b"><i class="st ok"></i>Đang làm bài (22/40)</span></div>
              <div class="tr"><span>005</span><span>Hoàng Văn Em</span><span>PC-06</span><span><i class="st ok"></i>Đang làm bài (<em class="cnt" data-count="33">33</em>/40)</span></div>
            </div>
            <div class="feed">
              <b>Hoạt động</b>
              <div class="fi f1"><i class="st done"></i>PC-03 đã nộp bài · Lê Văn Cường</div>
              <div class="fi f2"><i class="st warn"></i>PC-05 mất kết nối · đồng hồ của thí sinh tạm dừng</div>
              <div class="fi f3"><i class="st ok"></i>PC-05 đã kết nối lại · tiếp tục làm bài</div>
              <div class="fi f4"><i class="st ok"></i>Đã sao lưu tự động</div>
            </div>
          </div>
        </div><?php endif; ?>
        <ul class="demo-notes"><li>Theo dõi từng máy: đang làm, mất kết nối, đã nộp</li><li>Nhắc nhở, cộng giờ, chuyển máy, thu bài</li><li>Xuất bảng điểm, bài tự luận đánh phách, biên bản, phân tích đề</li></ul>
      </div>

      <!-- ============ iTest-Editor: soạn đề ============ -->
      <div class="screen" data-screen="editor">
        <?php if ($img = screen_image('itest-editor')): ?><img src="<?= e($img) ?>" alt="Màn hình soạn đề của iTest-Editor" loading="lazy"><?php else: ?>
        <div class="win">
          <div class="win-bar"><span class="win-title">iTest-Editor · Kiểm tra giữa kỳ I – Toán 12</span><span class="chip ok">Đã lưu bản nháp</span><span class="chip">Đóng gói .itz</span></div>
          <div class="win-body split ed">
            <div class="qlist">
              <b>Câu hỏi <small>12/40 đã xong</small></b>
              <div class="qi done">1 · Một đáp án</div><div class="qi done">2 · Đúng/Sai nhiều ý</div><div class="qi sel">3 · Một đáp án</div><div class="qi">4 · Nhiều đáp án</div><div class="qi">5 · Tự luận</div>
              <div class="prog"><span></span></div><small>Tổng điểm <b class="good">10 / 10</b></small>
            </div>
            <div class="form">
              <label>Nội dung câu hỏi</label>
              <div class="tbox"><span class="type">Giá trị lớn nhất của hàm số y = −x² + 4x trên đoạn [0; 3] là</span><i class="caret"></i></div>
              <label>Phương án (chọn đáp án đúng)</label>
              <div class="eopts"><span>A. 3</span><span class="key">B. 4</span><span>C. 0</span><span>D. 5</span></div>
              <div class="tools"><span>∑ Công thức</span><span>🖼 Ảnh</span><span>🔊 Âm thanh</span><span>Nhập từ Word</span><span>Xuất PDF</span></div>
            </div>
          </div>
        </div><?php endif; ?>
        <ul class="demo-notes"><li>Một đáp án, nhiều đáp án, Đúng/Sai, tự luận, hình ảnh, âm thanh</li><li>Gõ công thức toán, nhập nhanh từ Word và Excel</li><li>Tự chia điểm, tự lưu nháp, đóng gói đề có mật khẩu</li></ul>
      </div>
    </div>
  </div>
</section>

<section id="tinh-nang" class="sec alt">
  <div class="head"><span class="kicker">Tính năng</span><h2>Đủ cho một buổi thi thật</h2></div>
  <div class="feats">
    <div class="feat"><h3>Chế độ thi khoá máy</h3><p>Toàn màn hình, chặn chuyển cửa sổ. Thí sinh chỉ thoát được khi giám thị kết thúc phiên.</p></div>
    <div class="feat"><h3>Tự tìm phòng thi</h3><p>Máy thí sinh tự thấy phòng thi trong mạng LAN; vào phòng bằng mã phòng do giám thị đọc.</p></div>
    <div class="feat"><h3>Trộn đề nhiều mã</h3><p>Đảo câu và đảo phương án theo từng mã đề; bảng phân tích đề quy về câu gốc.</p></div>
    <div class="feat"><h3>Không mất bài</h3><p>Bài làm lưu ở cả hai phía. Mất mạng, mất điện hay máy chủ gặp sự cố đều khôi phục được.</p></div>
    <div class="feat"><h3>Máy chủ tự kiểm tra</h3><p>Thời gian nộp, câu trả lời và phiên của thí sinh đều do máy chủ kiểm tra; điểm chỉ hiện khi giám thị cho phép.</p></div>
    <div class="feat"><h3>Xuất kết quả đầy đủ</h3><p>Bảng điểm, bài tự luận đánh phách, biên bản phòng thi, và điền điểm vào file mẫu của phần mềm quản lý trường.</p></div>
  </div>
</section>

<section id="dang-ky" class="sec">
  <div class="head"><span class="kicker">Cách nhận phần mềm</span><h2>Dành riêng cho trường học</h2>
    <p>iTest được cấp cho từng trường qua chứng chỉ sử dụng ghi tên Sở và tên trường.</p></div>
  <ol class="flow">
    <li><b>Đăng ký tài khoản</b><span>Nhập tên Sở, tên trường và email của người phụ trách.</span></li>
    <li><b>Nhận email</b><span>Link tải có hiệu lực 24 giờ và file chứng chỉ (.itcert) đính kèm.</span></li>
    <li><b>Cài đặt</b><span>Bộ cài iTest-Server và iTest-Editor yêu cầu chọn file chứng chỉ. Máy thí sinh không cần.</span></li>
    <li><b>Sử dụng</b><span>Tên Sở và tên trường trên đề thi, biên bản, bảng điểm được lấy từ chứng chỉ.</span></li>
  </ol>
  <p class="center"><a class="btn" href="<?= $cta ?>"><?= $logged ? 'Vào trang tài khoản' : 'Đăng ký cho trường' ?></a></p>
</section>

<section id="hoi-dap" class="sec alt">
  <div class="head"><span class="kicker">Hỏi đáp</span><h2>Câu hỏi thường gặp</h2></div>
  <div class="faq">
    <details><summary>Link tải hết hạn thì làm thế nào?</summary><p>Đăng nhập, vào mục Tài khoản và bấm “Gửi link tải qua email”. Link mới có hiệu lực 24 giờ.</p></details>
    <details><summary>Mất file chứng chỉ hoặc chứng chỉ hết hạn?</summary><p>Trong mục Tài khoản, bấm “Gửi lại chứng chỉ qua email”. Số lượt lấy lại có giới hạn. Trên máy đã cài, mở iTest-Server và bấm “Đổi chứng chỉ…” để nạp file mới.</p></details>
    <details><summary>Phòng máy không có Internet có dùng được không?</summary><p>Được. Chỉ cần Internet để tải bộ cài và nhận chứng chỉ. Trong giờ thi, các máy chỉ cần cùng một mạng LAN.</p></details>
    <details><summary>Máy thí sinh có cần chứng chỉ không?</summary><p>Không. Máy thí sinh tự kiểm tra chứng chỉ của máy giám thị khi vào phòng thi.</p></details>
    <details><summary>Windows hoặc trình duyệt cảnh báo khi tải bộ cài?</summary><p>Bộ cài chưa được ký số nên có thể hiện cảnh báo. Chọn “Giữ lại” trong trình duyệt và “Vẫn chạy” (Run anyway) khi cài.</p></details>
  </div>
</section>
<?php page_footer();
