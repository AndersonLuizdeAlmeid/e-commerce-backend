-- Fase 4.2 — pedidos, itens, histórico e valor da embalagem
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0017__customer_addresses.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0017__customer_addresses.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0018__ordering.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0018__ordering.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0018__ordering.sql
-- ======================================================================
-- Fase 4.2 — pedidos (RF-040..043, RF-046, RF-047). O pedido guarda cópia de tudo o que pode mudar depois: peça,
-- preço, endereço, frete e presente. Nenhuma linha aponta para o endereço nem para a sacola.
CREATE SCHEMA ordering;

CREATE SEQUENCE ordering.order_number_seq;

CREATE TABLE ordering.orders (
    id                       uuid PRIMARY KEY,
    number                   bigint NOT NULL UNIQUE DEFAULT nextval('ordering.order_number_seq'),
    customer_id              uuid NOT NULL REFERENCES customers.customers(id),
    status                   text NOT NULL CHECK (status IN ('PendingPayment','PaymentConfirmed','Preparing','Shipped','Delivered','Cancelled')),
    subtotal                 numeric(12,2) NOT NULL CHECK (subtotal > 0),
    shipping_fee             numeric(12,2) NOT NULL CHECK (shipping_fee >= 0),
    gift_wrap_price          numeric(12,2) NOT NULL DEFAULT 0 CHECK (gift_wrap_price >= 0),
    total                    numeric(12,2) NOT NULL CHECK (total = subtotal + shipping_fee + gift_wrap_price),
    -- frete escolhido
    shipping_service         text NOT NULL,
    shipping_service_name    text NOT NULL,
    shipping_days            smallint NULL,
    shipping_is_fallback     boolean NOT NULL DEFAULT false,
    -- endereço de entrega
    ship_recipient           text NOT NULL,
    ship_postal_code         char(8) NOT NULL,
    ship_street              text NOT NULL,
    ship_number              text NOT NULL,
    ship_complement          text NULL,
    ship_district            text NOT NULL,
    ship_city                text NOT NULL,
    ship_state               char(2) NOT NULL,
    -- presente (RF-047, entrega 4 §3 P1)
    is_gift                  boolean NOT NULL DEFAULT false,
    gift_wrap                boolean NOT NULL DEFAULT false,
    gift_message             text NULL,
    tracking_code            text NULL,
    cancellation_reason      text NULL CHECK (cancellation_reason IN ('CustomerRequest','AdminRequest','PaymentTimeout','PaymentRejected')),
    notes                    text NULL,
    idempotency_key          text NULL,
    payment_due_at           timestamptz NOT NULL,
    payment_reminder_sent_at timestamptz NULL,
    version                  int NOT NULL DEFAULT 0,
    placed_at                timestamptz NOT NULL,
    updated_at               timestamptz NOT NULL,
    UNIQUE (customer_id, idempotency_key),
    CHECK (gift_wrap = false OR is_gift),
    CHECK (gift_message IS NULL OR is_gift),
    CHECK ((status = 'Cancelled') = (cancellation_reason IS NOT NULL))
);
CREATE INDEX ix_orders_customer ON ordering.orders(customer_id, placed_at DESC);
CREATE INDEX ix_orders_status ON ordering.orders(status, placed_at DESC);
-- Jobs de 24 h e 48 h (arquivo 25 §2).
CREATE INDEX ix_orders_payment_due ON ordering.orders(payment_due_at) WHERE status = 'PendingPayment';

CREATE TABLE ordering.order_items (
    order_id      uuid NOT NULL REFERENCES ordering.orders(id),
    variant_id    uuid NOT NULL,
    product_id    uuid NOT NULL,
    sku           text NOT NULL,
    product_name  text NOT NULL,
    color_name    text NULL,
    size_name     text NULL,
    image_url     text NULL,
    unit_price    numeric(12,2) NOT NULL CHECK (unit_price >= 0),
    quantity      int NOT NULL CHECK (quantity BETWEEN 1 AND 99),
    position      smallint NOT NULL,
    PRIMARY KEY (order_id, variant_id)
);

CREATE TABLE ordering.order_status_history (
    id           uuid PRIMARY KEY,
    order_id     uuid NOT NULL REFERENCES ordering.orders(id),
    from_status  text NULL,
    to_status    text NOT NULL,
    changed_by   uuid NULL,                          -- NULL = sistema (job, webhook)
    changed_at   timestamptz NOT NULL,
    note         text NULL
);
CREATE INDEX ix_order_status_history_order ON ordering.order_status_history(order_id, changed_at);

-- Configuração do pedido, uma linha só. A embalagem começa grátis até a D-F08 (entrega 4 §3 P1).
CREATE TABLE ordering.settings (
    id               smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    gift_wrap_price  numeric(12,2) NOT NULL CHECK (gift_wrap_price >= 0),
    updated_at       timestamptz NOT NULL
);
INSERT INTO ordering.settings (id, gift_wrap_price, updated_at) VALUES (1, 0, now());

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0018__ordering.sql', now() AT TIME ZONE 'utc');

COMMIT;
