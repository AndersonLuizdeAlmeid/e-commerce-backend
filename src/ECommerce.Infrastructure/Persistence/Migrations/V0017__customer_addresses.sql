-- Fase 4.1 — caderno de endereços do cliente (RF-007). O pedido copia o endereço na compra, então nenhum pedido
-- aponta para esta tabela e o cliente pode apagar qualquer endereço.
CREATE TABLE customers.addresses (
    id            uuid PRIMARY KEY,
    customer_id   uuid NOT NULL REFERENCES customers.customers(id),
    recipient     text NOT NULL,
    postal_code   char(8) NOT NULL CHECK (postal_code ~ '^[0-9]{8}$'),
    street        text NOT NULL,
    number        text NOT NULL,
    complement    text NULL,
    district      text NOT NULL,
    city          text NOT NULL,
    state         char(2) NOT NULL,
    is_default    boolean NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
CREATE INDEX ix_addresses_customer ON customers.addresses(customer_id);
-- Um padrão por cliente; o domínio garante, o índice é a última barreira.
CREATE UNIQUE INDEX ux_addresses_default ON customers.addresses(customer_id) WHERE is_default;
