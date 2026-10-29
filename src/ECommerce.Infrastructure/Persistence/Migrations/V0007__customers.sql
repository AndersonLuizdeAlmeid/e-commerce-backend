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
