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
