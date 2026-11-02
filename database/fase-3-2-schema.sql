-- Fase 3.2 — sacola do cliente
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0013__inventory_stock_movements.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0013__inventory_stock_movements.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0014__carts.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0014__carts.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0014__carts.sql
-- ======================================================================
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

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0014__carts.sql', now() AT TIME ZONE 'utc');

COMMIT;
