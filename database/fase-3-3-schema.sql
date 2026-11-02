-- Fase 3.3 — configuração de frete
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0014__carts.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0014__carts.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0015__shipping_settings.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0015__shipping_settings.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0015__shipping_settings.sql
-- ======================================================================
-- Fase 3.3 — configuração de frete da loja (RF-058). Linha única, criada pelo painel: sem ela, a cotação avisa que
-- o frete não está configurado, em vez de inventar CEP de origem ou preço. As regras entram na Fase 3.4.
CREATE SCHEMA shipping;

CREATE TABLE shipping.settings (
    id                     smallint PRIMARY KEY CHECK (id = 1),
    origin_postal_code     char(8) NOT NULL CHECK (origin_postal_code ~ '^[0-9]{8}$'),
    handling_days          smallint NOT NULL DEFAULT 1 CHECK (handling_days >= 0),
    fallback_pac_price     numeric(12,2) NOT NULL CHECK (fallback_pac_price >= 0),
    fallback_sedex_price   numeric(12,2) NOT NULL CHECK (fallback_sedex_price >= 0),
    box_weight_grams       int NOT NULL CHECK (box_weight_grams >= 0),
    box_height_cm          numeric(6,1) NOT NULL CHECK (box_height_cm > 0),
    box_width_cm           numeric(6,1) NOT NULL CHECK (box_width_cm > 0),
    box_length_cm          numeric(6,1) NOT NULL CHECK (box_length_cm > 0),
    updated_at             timestamptz NOT NULL
);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0015__shipping_settings.sql', now() AT TIME ZONE 'utc');

COMMIT;
