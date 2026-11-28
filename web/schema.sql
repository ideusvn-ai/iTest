-- Cơ sở dữ liệu trang web iTest (MySQL 5.7+ / MariaDB 10.3+).
-- Chạy một lần:  mysql -u <user> -p <database> < schema.sql

CREATE TABLE IF NOT EXISTS users (
    id            INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    email         VARCHAR(190) NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    contact_name  VARCHAR(120) NOT NULL,
    phone         VARCHAR(30)  NOT NULL DEFAULT '',
    department    VARCHAR(200) NOT NULL,
    school        VARCHAR(200) NOT NULL,
    created_at    DATETIME NOT NULL,
    updated_at    DATETIME NOT NULL,
    last_login_at DATETIME NULL,
    UNIQUE KEY uq_users_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Mỗi chứng chỉ đã cấp (lần đăng ký, mỗi lần lấy lại, mỗi lần đổi tên trường/Sở).
CREATE TABLE IF NOT EXISTS certificates (
    id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    user_id    INT UNSIGNED NOT NULL,
    cert_id    VARCHAR(40)  NOT NULL,
    department VARCHAR(200) NOT NULL,
    school     VARCHAR(200) NOT NULL,
    reason     VARCHAR(20)  NOT NULL,          -- register | reissue | profile
    issued_on  DATE NOT NULL,
    expires_on DATE NULL,
    created_at DATETIME NOT NULL,
    ip         VARCHAR(45) NOT NULL DEFAULT '',
    KEY ix_cert_user (user_id, created_at),
    CONSTRAINT fk_cert_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Link tải có thời hạn. Chỉ lưu SHA-256 của mã trong link, không lưu mã gốc.
CREATE TABLE IF NOT EXISTS download_tokens (
    id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    user_id    INT UNSIGNED NOT NULL,
    token_hash CHAR(64) NOT NULL,
    expires_at DATETIME NOT NULL,
    created_at DATETIME NOT NULL,
    hits       INT UNSIGNED NOT NULL DEFAULT 0,
    UNIQUE KEY uq_dl_token (token_hash),
    KEY ix_dl_user (user_id, created_at),
    CONSTRAINT fk_dl_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS password_resets (
    id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    user_id    INT UNSIGNED NOT NULL,
    token_hash CHAR(64) NOT NULL,
    expires_at DATETIME NOT NULL,
    used_at    DATETIME NULL,
    created_at DATETIME NOT NULL,
    UNIQUE KEY uq_reset_token (token_hash),
    CONSTRAINT fk_reset_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Nhật ký để giới hạn tần suất (đăng nhập sai, đăng ký, quên mật khẩu).
CREATE TABLE IF NOT EXISTS events (
    id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    kind       VARCHAR(30)  NOT NULL,
    subject    VARCHAR(190) NOT NULL DEFAULT '',
    ip         VARCHAR(45)  NOT NULL DEFAULT '',
    created_at DATETIME NOT NULL,
    KEY ix_events (kind, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
