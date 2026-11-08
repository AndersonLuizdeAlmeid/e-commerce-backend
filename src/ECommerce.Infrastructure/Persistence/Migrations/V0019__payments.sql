-- Fase 4.3 — pagamentos (arquivo 21). Um pedido pode ter várias tentativas; no máximo uma aprovada (RN-102).
CREATE SCHEMA payments;

CREATE TABLE payments.payments (
    id                   uuid PRIMARY KEY,
    order_id             uuid NOT NULL REFERENCES ordering.orders(id),
    provider             text NOT NULL,
    provider_payment_id  text NULL,
    method               text NOT NULL CHECK (method IN ('Pix','CreditCard')),
    status               text NOT NULL CHECK (status IN ('Pending','Approved','Rejected','Expired','Cancelled','Refunded','RefundFailed')),
    status_detail        text NULL,
    amount               numeric(12,2) NOT NULL CHECK (amount > 0),
    installments         smallint NULL CHECK (installments BETWEEN 1 AND 24),
    pix_qr_code          text NULL,
    pix_qr_code_base64   text NULL,
    expires_at           timestamptz NULL,
    approved_at          timestamptz NULL,
    idempotency_key      text NULL,
    version              int NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL,
    UNIQUE (provider, provider_payment_id),
    UNIQUE (order_id, idempotency_key)
);
CREATE INDEX ix_payments_order ON payments.payments(order_id, created_at DESC);
CREATE UNIQUE INDEX ux_payments_one_approved ON payments.payments(order_id) WHERE status = 'Approved';  -- RN-102
-- Reconciliação: pendentes sem notificação (arquivo 21 §5.5).
CREATE INDEX ix_payments_pending ON payments.payments(created_at) WHERE status = 'Pending';

-- Log das notificações da plataforma; reentrega do mesmo evento é ignorada pela chave primária.
CREATE TABLE payments.payment_events (
    provider           text NOT NULL,
    provider_event_id  text NOT NULL,
    provider_payment_id text NOT NULL,
    payload            jsonb NOT NULL,
    received_at        timestamptz NOT NULL,
    PRIMARY KEY (provider, provider_event_id)
);
