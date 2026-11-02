-- Fase 3.3 — configuração de frete de exemplo, sem regras (SOMENTE Development)
-- GERADO por database/gerar-scripts.sh a partir de src/ECommerce.Infrastructure/Persistence (não edite aqui).
-- Executar no DBeaver como script (Alt+X) conectado ao banco 'ecommerce'. Tudo roda em uma transação:
-- se algo falhar, nada é aplicado. Cada script é registrado em public.seedversions, então o
-- ECommerce.Migrator e o /health/ready o enxergam como já aplicado.

BEGIN;

CREATE TABLE IF NOT EXISTS public.seedversions (
    schemaversionsid serial NOT NULL,
    scriptname character varying(255) NOT NULL,
    applied timestamp without time zone NOT NULL,
    CONSTRAINT pk_seedversions_id PRIMARY KEY (schemaversionsid)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0015__shipping_settings.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0015__shipping_settings.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.seedversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Seeds.Development.S0005__example_shipping.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'S0005__example_shipping.sql';
    END IF;
END
$$;

-- ======================================================================
-- S0005__example_shipping.sql
-- ======================================================================
-- Configuração de frete de exemplo — somente em Development (arquivo 05 §5). Sem nenhuma regra (arquivo 20, D08).
INSERT INTO shipping.settings
    (id, origin_postal_code, handling_days, fallback_pac_price, fallback_sedex_price,
     box_weight_grams, box_height_cm, box_width_cm, box_length_cm, updated_at)
VALUES (1, '01310100', 1, 25.00, 45.00, 100, 4, 12, 16, now());

INSERT INTO public.seedversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Seeds.Development.S0005__example_shipping.sql', now() AT TIME ZONE 'utc');

COMMIT;
