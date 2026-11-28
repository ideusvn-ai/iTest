-- Bản lược đồ tương đương cho SQLite, CHỈ dùng khi chạy thử tự động trên máy không có MySQL.
CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, contact_name TEXT NOT NULL,
  phone TEXT NOT NULL DEFAULT '', department TEXT NOT NULL, school TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, last_login_at TEXT NULL);
CREATE TABLE certificates (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, cert_id TEXT NOT NULL, department TEXT NOT NULL, school TEXT NOT NULL,
  reason TEXT NOT NULL, issued_on TEXT NOT NULL, expires_on TEXT NULL, created_at TEXT NOT NULL, ip TEXT NOT NULL DEFAULT '');
CREATE TABLE download_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, token_hash TEXT NOT NULL UNIQUE, expires_at TEXT NOT NULL,
  created_at TEXT NOT NULL, hits INTEGER NOT NULL DEFAULT 0);
CREATE TABLE password_resets (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, token_hash TEXT NOT NULL UNIQUE, expires_at TEXT NOT NULL,
  used_at TEXT NULL, created_at TEXT NOT NULL);
CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, subject TEXT NOT NULL DEFAULT '', ip TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL);
