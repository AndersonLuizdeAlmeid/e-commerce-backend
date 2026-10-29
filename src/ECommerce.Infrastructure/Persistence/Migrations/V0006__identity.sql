-- Fase 2A — contas: usuários, sessões (refresh tokens) e tokens de e-mail/senha (arquivos 05 §3 e 08)
CREATE SCHEMA identity;

CREATE TABLE identity.users (
    id                     uuid PRIMARY KEY,
    email                  citext NOT NULL UNIQUE,
    password_hash          text   NOT NULL,
    role                   text   NOT NULL CHECK (role IN ('Customer','Admin')),
    is_active              boolean NOT NULL DEFAULT true,
    email_confirmed_at     timestamptz NULL,
    failed_login_attempts  int     NOT NULL DEFAULT 0,
    lockout_end_utc        timestamptz NULL,
    security_stamp         uuid    NOT NULL,
    version                int     NOT NULL DEFAULT 0,
    created_at             timestamptz NOT NULL,
    updated_at             timestamptz NOT NULL
);
CREATE INDEX ix_users_active_admins ON identity.users(role) WHERE role = 'Admin' AND is_active;

-- Refresh token rotativo: só o hash SHA-256 é guardado. A família inteira expira em absolute_expires_at (máx. 30 dias).
CREATE TABLE identity.refresh_tokens (
    id                   uuid PRIMARY KEY,
    user_id              uuid NOT NULL REFERENCES identity.users(id),
    token_hash           text NOT NULL UNIQUE,
    family_id            uuid NOT NULL,
    expires_at           timestamptz NOT NULL,
    absolute_expires_at  timestamptz NOT NULL,
    created_at           timestamptz NOT NULL,
    created_by_ip        inet NULL,
    revoked_at           timestamptz NULL,
    replaced_by_id       uuid NULL
);
CREATE INDEX ix_refresh_tokens_user   ON identity.refresh_tokens(user_id);
CREATE INDEX ix_refresh_tokens_family ON identity.refresh_tokens(family_id);

-- Confirmação de e-mail e recuperação de senha (uso único, hash SHA-256)
CREATE TABLE identity.user_tokens (
    id          uuid PRIMARY KEY,
    user_id     uuid NOT NULL REFERENCES identity.users(id),
    purpose     text NOT NULL CHECK (purpose IN ('EmailConfirmation','PasswordReset')),
    token_hash  text NOT NULL UNIQUE,
    expires_at  timestamptz NOT NULL,
    used_at     timestamptz NULL,
    created_at  timestamptz NOT NULL
);
CREATE INDEX ix_user_tokens_user ON identity.user_tokens(user_id, purpose);
