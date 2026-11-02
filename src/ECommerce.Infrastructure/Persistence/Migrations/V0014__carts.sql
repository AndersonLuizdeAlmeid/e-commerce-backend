-- Fase 3.2 — sacola do cliente (RF-030, RF-031). Não guarda preço nem reserva estoque.
CREATE SCHEMA carts;

CREATE TABLE carts.carts (
    id           uuid PRIMARY KEY,
    customer_id  uuid NOT NULL UNIQUE REFERENCES customers.customers(id),
    updated_at   timestamptz NOT NULL
);

CREATE TABLE carts.cart_items (
    cart_id     uuid NOT NULL REFERENCES carts.carts(id) ON DELETE CASCADE,
    variant_id  uuid NOT NULL REFERENCES catalog.product_variants(id),
    quantity    int  NOT NULL CHECK (quantity BETWEEN 1 AND 99),
    added_at    timestamptz NOT NULL,
    PRIMARY KEY (cart_id, variant_id)
);
CREATE INDEX ix_cart_items_variant ON carts.cart_items(variant_id);
