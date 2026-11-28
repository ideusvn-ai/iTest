<?php
// CHỈ dùng để chạy thử tự động trên máy không có MySQL (xem tests/run.sh). Trang thật luôn dùng MysqliDatabase.
final class SqliteDatabase implements Database
{
    private PDO $pdo;

    public function __construct(string $file)
    {
        $new = !file_exists($file);
        $this->pdo = new PDO('sqlite:' . $file, null, null, [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION]);
        $this->pdo->exec('PRAGMA busy_timeout = 5000');
        if ($new) { $this->pdo->exec(file_get_contents(__DIR__ . '/schema.sqlite.sql')); }
    }

    private function run(string $sql, array $params): PDOStatement
    {
        $stmt = $this->pdo->prepare($sql);
        $stmt->execute(array_values($params));
        return $stmt;
    }

    public function exec(string $sql, array $params = []): int { return $this->run($sql, $params)->rowCount(); }
    public function insert(string $sql, array $params = []): int { $this->run($sql, $params); return (int)$this->pdo->lastInsertId(); }
    public function all(string $sql, array $params = []): array { return $this->run($sql, $params)->fetchAll(PDO::FETCH_ASSOC); }
    public function one(string $sql, array $params = []): ?array { $r = $this->all($sql, $params); return $r[0] ?? null; }
    public function value(string $sql, array $params = []) { $r = $this->one($sql, $params); return $r === null ? null : reset($r); }
}
