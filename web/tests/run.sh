#!/usr/bin/env bash
# Chạy thử toàn bộ luồng của trang web bằng máy chủ PHP dựng sẵn, SQLite và email ghi ra file (không cần MySQL, không gửi thư thật).
#   bash tests/run.sh
set -u
cd "$(dirname "$0")/.."
export ITEST_TEST_DIR="$(mktemp -d)"
export ITEST_CONFIG="$PWD/tests/config.test.php"
PORT=8765; BASE="http://127.0.0.1:$PORT"; export ITEST_TEST_URL="$BASE"
if [ -z "${ITEST_TEST_KEY:-}" ]; then openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$ITEST_TEST_DIR/key.pem" 2>/dev/null; fi
mkdir -p "$ITEST_TEST_DIR/downloads"
head -c 300000 /dev/urandom > "$ITEST_TEST_DIR/downloads/iTest-Setup.exe"
head -c 200000 /dev/urandom > "$ITEST_TEST_DIR/downloads/iTest-Server-Setup.exe"
php -S 127.0.0.1:$PORT -t . >"$ITEST_TEST_DIR/server.log" 2>&1 & SERVER=$!
trap 'kill $SERVER 2>/dev/null; rm -rf "$ITEST_TEST_DIR"' EXIT
sleep 1
JAR="$ITEST_TEST_DIR/jar"; PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ĐẠT   $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  HỎNG  $1"; }
check(){ if eval "$2"; then ok "$1"; else bad "$1"; fi; }
get()  { curl -s -b "$JAR" -c "$JAR" "$BASE/$1"; }
code() { curl -s -o /dev/null -w '%{http_code}' -b "$JAR" -c "$JAR" "$BASE/$1"; }
csrf() { get "$1" | grep -o 'name="csrf" value="[a-f0-9]*"' | head -1 | sed 's/.*value="//;s/"//'; }
post() { local page="$1"; shift; curl -s -L -b "$JAR" -c "$JAR" "$@" "$BASE/$page"; }
mails(){ ls "$ITEST_TEST_DIR/mail" 2>/dev/null | wc -l; }
lastmail(){ ls -t "$ITEST_TEST_DIR/mail"/* | head -1; }
# Giải mã phần HTML (base64) của email mới nhất
mailhtml(){ php -r '$m=file_get_contents($argv[1]); preg_match_all("~Content-Type: text/html.*?\r\n\r\n(.*?)\r\n--~s",$m,$x); echo base64_decode(preg_replace("/\s+/","",$x[1][0]));' "$(lastmail)"; }

echo "== Trang công khai"
check "trang chủ mở được và có hình động giao diện" '[ "$(code index.php)" = 200 ] && get index.php | grep -q "data-demo"'
check "trang tài khoản đòi đăng nhập" 'get account.php -o /dev/null; [ "$(curl -s -o /dev/null -w "%{http_code}" "$BASE/account.php")" = 303 ]'
check "link tải bịa đặt bị từ chối" '[ "$(code "download.php?t=$(printf "a%.0s" $(seq 48))")" = 410 ]'

echo "== Đăng ký"
T=$(csrf register.php)
OUT=$(post register.php --data-urlencode "csrf=$T" --data-urlencode "email=bad" --data-urlencode "password=123" --data-urlencode "password2=123")
check "đăng ký thiếu thông tin bị báo lỗi, không tạo tài khoản" 'echo "$OUT" | grep -q "Email không hợp lệ" && [ "$(mails)" = 0 ]'
check "gửi form không có mã CSRF bị từ chối" '[ "$(curl -s -o /dev/null -w "%{http_code}" -b "$JAR" -d "email=a@b.vn" "$BASE/register.php")" = 400 ]'
T=$(csrf register.php)
OUT=$(post register.php --data-urlencode "csrf=$T" --data-urlencode "email=PhuTrach@Truong.edu.vn" --data-urlencode "contact_name=Nguyễn Văn An" \
  --data-urlencode "phone=0901 234 567" --data-urlencode "department=Sở Giáo dục và Đào tạo Thử" --data-urlencode "school=Trường THPT <b>Thử</b> & Nghiệm" \
  --data-urlencode "password=matkhau-123" --data-urlencode "password2=matkhau-123" --data-urlencode "agree=1")
check "đăng ký thành công, vào trang tài khoản" 'echo "$OUT" | grep -q "Tài khoản của trường" && echo "$OUT" | grep -q "Đăng ký thành công"'
check "tên trường có ký tự HTML được hiển thị an toàn" 'echo "$OUT" | grep -q "Trường THPT &lt;b&gt;Thử&lt;/b&gt; &amp; Nghiệm" && ! echo "$OUT" | grep -q "<b>Thử</b>"'
check "đúng một email được gửi sau khi đăng ký" '[ "$(mails)" = 1 ]'
M=$(lastmail)
check "email gửi tới địa chỉ đã đăng ký (chữ thường) và đính kèm chứng chỉ" 'grep -q "^To: phutrach@truong.edu.vn" "$M" && grep -q "filename=\"ChungChi-iTest-Truong-THPT-b-Thu-b-Nghiem.itcert\"" "$M"'
LINK=$(mailhtml | grep -o "$BASE/download.php?t=[a-f0-9]*" | head -1)
check "email có link tải" '[ -n "$LINK" ]'

echo "== Chứng chỉ"
php -r '$m=file_get_contents($argv[1]); preg_match("~filename=\"[^\"]+\.itcert\"\r\n\r\n(.*?)\r\n--~s",$m,$x); file_put_contents($argv[2], base64_decode(preg_replace("/\s+/","",$x[1])));' "$M" "$ITEST_TEST_DIR/c.itcert"
KEY="${ITEST_TEST_KEY:-$ITEST_TEST_DIR/key.pem}"
VERIFY=$(php -r '$c=json_decode(file_get_contents($argv[1]),true); $d=base64_decode($c["data"]); $pub=openssl_pkey_get_details(openssl_pkey_get_private(file_get_contents($argv[2])))["key"];
  echo $c["format"],"|",openssl_verify($d,base64_decode($c["sig"]),$pub,OPENSSL_ALGO_SHA256),"|",$d;' "$ITEST_TEST_DIR/c.itcert" "$KEY")
check "chứng chỉ đúng định dạng và chữ ký RSA-SHA256 hợp lệ" 'echo "$VERIFY" | grep -q "^iTest-Certificate-1|1|"'
check "chứng chỉ ghi đúng tên Sở, tên trường, email đã nhập" 'echo "$VERIFY" | grep -q "\"department\":\"Sở Giáo dục và Đào tạo Thử\"" && echo "$VERIFY" | grep -q "\"school\":\"Trường THPT <b>Thử</b> & Nghiệm\"" && echo "$VERIFY" | grep -q "phutrach@truong.edu.vn"'
check "chứng chỉ có hạn dùng một năm" 'echo "$VERIFY" | grep -q "\"expires\":\"$(TZ=Asia/Ho_Chi_Minh date -d "+365 days" +%Y-%m-%d)\""'

echo "== Tải xuống theo link 24 giờ"
PAGE=$(curl -s "$LINK")
check "trang tải liệt kê các file đang có (không cần đăng nhập)" 'echo "$PAGE" | grep -q "iTest-Setup.exe" && echo "$PAGE" | grep -q "iTest-Server-Setup.exe" && ! echo "$PAGE" | grep -q "iTest-Editor-Setup.exe"'
curl -s -o "$ITEST_TEST_DIR/got.exe" "$LINK&f=iTest-Setup.exe"
check "file tải về giống hệt file trên máy chủ" 'cmp -s "$ITEST_TEST_DIR/got.exe" "$ITEST_TEST_DIR/downloads/iTest-Setup.exe"'
check "không tải được file ngoài danh sách (dò đường dẫn)" '[ "$(curl -s -o /dev/null -w "%{http_code}" "$LINK&f=../test.sqlite")" = 404 ] && [ "$(curl -s -o /dev/null -w "%{http_code}" "$LINK&f=key.pem")" = 404 ]'
# (Việc chặn truy cập thẳng vào storage/, lib/ do .htaccess của Apache đảm nhiệm; máy chủ PHP dựng sẵn không đọc .htaccess nên không thử ở đây.)
php -r '$p=new PDO("sqlite:".$argv[1]); $p->exec("UPDATE download_tokens SET expires_at = \"2020-01-01 00:00:00\"");' "$ITEST_TEST_DIR/test.sqlite"
check "link quá 24 giờ thì hết hạn, không tải được" '[ "$(curl -s -o /dev/null -w "%{http_code}" "$LINK")" = 410 ] && [ "$(curl -s -o /dev/null -w "%{http_code}" "$LINK&f=iTest-Setup.exe")" = 410 ]'

echo "== Tài khoản: link tải mới, lấy lại chứng chỉ, giới hạn lượt"
N=$(mails); T=$(csrf account.php)
OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=download")
check "xin link tải mới: gửi qua email" 'echo "$OUT" | grep -q "Link tải mới" && [ "$(mails)" = $((N+1)) ]'
NEW=$(mailhtml | grep -o "$BASE/download.php?t=[a-f0-9]*" | head -1)
check "link mới dùng được, khác link cũ" '[ "$NEW" != "$LINK" ] && [ "$(curl -s -o /dev/null -w "%{http_code}" "$NEW")" = 200 ]'
T=$(csrf account.php); post account.php --data-urlencode "csrf=$T" --data-urlencode "action=download" >/dev/null
N=$(mails); T=$(csrf account.php); OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=download")
check "quá số lượt xin link tải thì bị từ chối, không gửi thêm email" 'echo "$OUT" | grep -q "Đã hết lượt xin link tải" && [ "$(mails)" = "$N" ]'
N=$(mails); T=$(csrf account.php); OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=certificate")
check "lấy lại chứng chỉ: gửi qua email kèm file" 'echo "$OUT" | grep -q "Chứng chỉ đã được gửi" && [ "$(mails)" = $((N+1)) ] && grep -q "\.itcert" "$(lastmail)"'
T=$(csrf account.php)
OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=profile" --data-urlencode "contact_name=Trần Thị Bình" --data-urlencode "phone=0912345678" \
  --data-urlencode "department=Sở Giáo dục và Đào tạo Thử" --data-urlencode "school=Trường THPT Đổi Tên")
check "đổi tên trường: lưu và cấp chứng chỉ mới theo tên mới (tính một lượt)" 'echo "$OUT" | grep -q "Chứng chỉ mới theo tên" && echo "$OUT" | grep -q "Trường THPT Đổi Tên" && grep -q "ChungChi-iTest-Truong-THPT-Doi-Ten.itcert" "$(lastmail)"'
N=$(mails); T=$(csrf account.php); OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=certificate")
check "quá số lượt lấy lại chứng chỉ thì bị từ chối" 'echo "$OUT" | grep -q "Đã hết lượt lấy lại chứng chỉ" && [ "$(mails)" = "$N" ]'
T=$(csrf account.php)
OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=profile" --data-urlencode "contact_name=Trần Thị Bình" --data-urlencode "phone=0912345678" \
  --data-urlencode "department=Sở Giáo dục và Đào tạo Thử" --data-urlencode "school=Trường THPT Lại Đổi")
check "hết lượt thì không đổi được tên trường nữa" 'echo "$OUT" | grep -q "chưa đổi được tên Sở" && ! get account.php | grep -q "Chứng chỉ ghi: Trường THPT Lại Đổi"'
T=$(csrf account.php)
OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=profile" --data-urlencode "contact_name=Lê Văn Cường" --data-urlencode "phone=0999888777" \
  --data-urlencode "department=Sở Giáo dục và Đào tạo Thử" --data-urlencode "school=Trường THPT Đổi Tên")
check "vẫn đổi được người phụ trách và số điện thoại" 'echo "$OUT" | grep -q "Đã lưu thông tin" && echo "$OUT" | grep -q "Lê Văn Cường"'

echo "== Mật khẩu và đăng nhập"
T=$(csrf account.php)
OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=password" --data-urlencode "old_password=sai" --data-urlencode "password=matkhau-moi-1" --data-urlencode "password2=matkhau-moi-1")
check "đổi mật khẩu với mật khẩu hiện tại sai bị từ chối" 'echo "$OUT" | grep -q "Mật khẩu hiện tại không đúng"'
T=$(csrf account.php)
OUT=$(post account.php --data-urlencode "csrf=$T" --data-urlencode "action=password" --data-urlencode "old_password=matkhau-123" --data-urlencode "password=matkhau-moi-1" --data-urlencode "password2=matkhau-moi-1")
check "đổi mật khẩu thành công và có email báo" 'echo "$OUT" | grep -q "Đã đổi mật khẩu" && grep -q "Subject: =?UTF-8?B?" "$(lastmail)"'
T=$(csrf account.php); post logout.php --data-urlencode "csrf=$T" >/dev/null
check "đăng xuất xong không vào được trang tài khoản" '[ "$(code account.php)" = 303 ]'
T=$(csrf login.php); OUT=$(post login.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" --data-urlencode "password=matkhau-123")
check "mật khẩu cũ không còn dùng được" 'echo "$OUT" | grep -q "Email hoặc mật khẩu không đúng"'
T=$(csrf login.php); OUT=$(post login.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" --data-urlencode "password=matkhau-moi-1")
check "đăng nhập bằng mật khẩu mới" 'echo "$OUT" | grep -q "Tài khoản của trường"'
T=$(csrf account.php); post logout.php --data-urlencode "csrf=$T" >/dev/null

echo "== Quên mật khẩu"
N=$(mails); T=$(csrf forgot.php); OUT=$(post forgot.php --data-urlencode "csrf=$T" --data-urlencode "email=khong-co@example.vn")
check "email không có tài khoản: trả lời như nhau, không gửi thư" 'echo "$OUT" | grep -q "Nếu email này có tài khoản" && [ "$(mails)" = "$N" ]'
T=$(csrf forgot.php); post forgot.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" >/dev/null
RESET=$(mailhtml | grep -o "$BASE/reset.php?t=[a-f0-9]*" | head -1)
check "email đặt lại mật khẩu có link" '[ -n "$RESET" ]'
RT=${RESET##*t=}; T=$(csrf "reset.php?t=$RT")
OUT=$(post reset.php --data-urlencode "csrf=$T" --data-urlencode "t=$RT" --data-urlencode "password=matkhau-cuoi-9" --data-urlencode "password2=matkhau-cuoi-9")
check "đặt mật khẩu mới qua link" 'echo "$OUT" | grep -q "Đã đặt mật khẩu mới"'
check "link đặt lại chỉ dùng được một lần" 'get "reset.php?t=$RT" | grep -q "đã hết hạn hoặc đã được dùng"'
T=$(csrf login.php); OUT=$(post login.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" --data-urlencode "password=matkhau-cuoi-9")
check "đăng nhập bằng mật khẩu vừa đặt lại" 'echo "$OUT" | grep -q "Tài khoản của trường"'
T=$(csrf account.php); post logout.php --data-urlencode "csrf=$T" >/dev/null

echo "== Chống dò mật khẩu và đăng ký trùng"
for i in 1 2 3; do T=$(csrf login.php); post login.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" --data-urlencode "password=sai-$i" >/dev/null; done
T=$(csrf login.php); OUT=$(post login.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" --data-urlencode "password=matkhau-cuoi-9")
check "sai mật khẩu quá số lần: khoá tạm cả khi nhập đúng" 'echo "$OUT" | grep -q "Đăng nhập sai quá nhiều lần"'
N=$(mails); T=$(csrf register.php)
OUT=$(post register.php --data-urlencode "csrf=$T" --data-urlencode "email=phutrach@truong.edu.vn" --data-urlencode "contact_name=Người Khác" --data-urlencode "phone=0900000000" \
  --data-urlencode "department=Sở Giáo dục Khác" --data-urlencode "school=Trường Khác ABC" --data-urlencode "password=matkhau-123" --data-urlencode "password2=matkhau-123" --data-urlencode "agree=1")
check "email đã có tài khoản không đăng ký lại được" 'echo "$OUT" | grep -q "Email này đã có tài khoản" && [ "$(mails)" = "$N" ]'
check "nhật ký máy chủ không có lỗi PHP" '! grep -Ei "PHP (Warning|Notice|Fatal|Deprecated|Parse)" "$ITEST_TEST_DIR/server.log"'

echo; echo "Kết quả: $PASS đạt, $FAIL hỏng"
[ "$FAIL" = 0 ] || { echo "--- nhật ký máy chủ ---"; grep -v "Accepted\|Closing\|\[200\]\|\[303\]" "$ITEST_TEST_DIR/server.log" | tail -20; exit 1; }
