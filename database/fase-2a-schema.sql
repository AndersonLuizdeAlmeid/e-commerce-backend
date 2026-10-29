-- Fase 2A — contas: identity (usuários, sessões, tokens), customers, outbox e log de e-mails
-- GERADO por database/gerar-scripts.sh a partir de src/ECommerce.Infrastructure/Persistence (não edite aqui).
-- Executar no DBeaver como script (Alt+X) conectado ao banco 'ecommerce'. Tudo roda em uma transação:
-- se algo falhar, nada é aplicado. Cada script é registrado em public.schemaversions, então o
-- ECommerce.Migrator e o /health/ready o enxergam como já aplicado.

BEGIN;

CREATE TABLE IF NOT EXISTS public.schemaversions (
    schemaversionsid serial NOT NULL,
    scriptname character varying(255) NOT NULL,
    applied timestamp without time zone NOT NULL,
    CONSTRAINT pk_schemaversions_id PRIMARY KEY (schemaversionsid)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0005__seed_colors_and_sizes.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0005__seed_colors_and_sizes.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0006__identity.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0006__identity.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0007__customers.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0007__customers.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0008__messaging_outbox_and_email_log.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0008__messaging_outbox_and_email_log.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0006__identity.sql
-- ======================================================================
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

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0006__identity.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0007__customers.sql
-- ======================================================================
-- Fase 2A — dados do cliente (RF-001, RF-005). Endereços (RF-007) entram com o checkout, na Fase 4.
CREATE SCHEMA customers;

CREATE TABLE customers.customers (
    id          uuid PRIMARY KEY REFERENCES identity.users(id),
    full_name   text NOT NULL,
    phone       text NULL,
    cpf         char(11) NULL UNIQUE,
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL
);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0007__customers.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0008__messaging_outbox_and_email_log.sql
-- ======================================================================
-- Fase 2A — Outbox transacional (arquivo 25 §1), antecipado da Fase 4 para os e-mails de conta não se perderem,
-- e registro dos e-mails enviados (arquivo 23 §4).
CREATE SCHEMA messaging;

CREATE TABLE messaging.outbox_messages (
    id               uuid PRIMARY KEY,
    type             text NOT NULL,
    payload          jsonb NOT NULL,
    occurred_at      timestamptz NOT NULL,
    attempts         int NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz NOT NULL,
    processed_at     timestamptz NULL,
    failed_at        timestamptz NULL,
    last_error       text NULL
);
CREATE INDEX ix_outbox_pending ON messaging.outbox_messages(next_attempt_at)
    WHERE processed_at IS NULL AND failed_at IS NULL;

CREATE SCHEMA notifications;

CREATE TABLE notifications.email_log (
    id                   uuid PRIMARY KEY,
    template             text NOT NULL,          -- "EM-01"
    recipient_masked     text NOT NULL,
    status               text NOT NULL CHECK (status IN ('Sent','Failed')),
    provider_message_id  text NULL,
    related_entity_id    uuid NULL,
    created_at           timestamptz NOT NULL
);
CREATE INDEX ix_email_log_created ON notifications.email_log(created_at DESC);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0008__messaging_outbox_and_email_log.sql', now() AT TIME ZONE 'utc');

COMMIT;
