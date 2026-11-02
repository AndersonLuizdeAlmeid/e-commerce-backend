-- Fase 3.1 — movimentações de estoque
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0012__catalog_showcase.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0012__catalog_showcase.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0013__inventory_stock_movements.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0013__inventory_stock_movements.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0013__inventory_stock_movements.sql
-- ======================================================================
-- Fase 3.1 — histórico de movimentações de estoque (RF-052). O saldo anterior não ganha movimentação retroativa.
CREATE TABLE inventory.stock_movements (
    id           uuid PRIMARY KEY,
    variant_id   uuid NOT NULL REFERENCES inventory.stock_items(variant_id),
    type         text NOT NULL CHECK (type IN ('In','Out','Adjustment','OrderPlaced','OrderCancelled')),
    quantity     int  NOT NULL CHECK (quantity <> 0),      -- delta com sinal
    reason       text NULL,
    order_id     uuid NULL,
    performed_by uuid NULL,
    created_at   timestamptz NOT NULL
);
CREATE INDEX ix_stock_movements_variant ON inventory.stock_movements(variant_id, created_at DESC);

-- Toda variação tem saldo: o admin de estoque e a Fase 4 contam com esta linha.
INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
SELECT v.id, 0, now()
  FROM catalog.product_variants v
ON CONFLICT (variant_id) DO NOTHING;

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0013__inventory_stock_movements.sql', now() AT TIME ZONE 'utc');

COMMIT;
