<?php
// Lớp truy cập cơ sở dữ liệu. Mọi câu lệnh đều là câu lệnh chuẩn bị sẵn (prepared statement), không ghép chuỗi từ dữ liệu người dùng.

interface Database
{
    /** Chạy câu lệnh ghi; trả về số dòng bị ảnh hưởng. */
    public function exec(string $sql, array $params = []): int;
    /** Chạy câu lệnh INSERT; trả về id vừa tạo. */
    public function insert(string $sql, array $params = []): int;
    /** Trả về mọi dòng (mảng kết hợp). */
    public function all(string $sql, array $params = []): array;
    /** Trả về dòng đầu tiên hoặc null. */
    public function one(string $sql, array $params = []): ?array;
    /** Trả về cột đầu tiên của dòng đầu tiên, hoặc null. */
    public function value(string $sql, array $params = []);
}

/** MySQL / MariaDB qua MySQLi. */
final class MysqliDatabase implements Database
{
    private mysqli $link;

    public function __construct(array $c)
    {
        mysqli_report(MYSQLI_REPORT_ERROR | MYSQLI_REPORT_STRICT);
        $this->link = new mysqli($c['host'] ?? 'localhost', $c['user'] ?? '', $c['pass'] ?? '', $c['name'] ?? '', (int)($c['port'] ?? 3306));
        $this->link->set_charset('utf8mb4');
    }

    private function run(string $sql, array $params): mysqli_stmt
    {
        $stmt = $this->link->prepare($sql);
        if ($params) {
            $types = '';
            $values = [];
            foreach ($params as $p) {
                if (is_bool($p)) { $p = (int)$p; }
                $types .= is_int($p) ? 'i' : (is_float($p) ? 'd' : 's');
                $values[] = $p;
            }
            $stmt->bind_param($types, ...$values);
        }
        $stmt->execute();
        return $stmt;
    }

    public function exec(string $sql, array $params = []): int
    {
        $stmt = $this->run($sql, $params);
        $n = $stmt->affected_rows;
        $stmt->close();
        return max(0, (int)$n);
    }

    public function insert(string $sql, array $params = []): int
    {
        $stmt = $this->run($sql, $params);
        $id = (int)$stmt->insert_id;
        $stmt->close();
        return $id;
    }

    public function all(string $sql, array $params = []): array
    {
        $stmt = $this->run($sql, $params);
        $res = $stmt->get_result();
        $rows = $res ? $res->fetch_all(MYSQLI_ASSOC) : [];
        $stmt->close();
        return $rows;
    }

    public function one(string $sql, array $params = []): ?array
    {
        $rows = $this->all($sql, $params);
        return $rows[0] ?? null;
    }

    public function value(string $sql, array $params = [])
    {
        $row = $this->one($sql, $params);
        return $row === null ? null : reset($row);
    }
}
