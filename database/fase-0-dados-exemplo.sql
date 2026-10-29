-- Fase 0 — categorias de exemplo (SOMENTE Development)
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0002__catalog_categories.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0002__catalog_categories.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.seedversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Seeds.Development.S0001__example_categories.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'S0001__example_categories.sql';
    END IF;
END
$$;

-- ======================================================================
-- S0001__example_categories.sql
-- ======================================================================
-- Catálogo de exemplo — aplicado somente em Development (arquivo 05 §5).
INSERT INTO catalog.categories (id, name, slug, description, seo_title, seo_description, is_active, created_at, updated_at) VALUES
  ('01920000-0000-7000-8000-000000000001', 'Camisetas', 'camisetas',
   'Camisetas de algodão em várias cores e tamanhos, do básico ao estampado.',
   'Camisetas de Algodão', 'Camisetas básicas e estampadas em algodão, com cores e tamanhos do PP ao XG.',
   true, now(), now()),
  ('01920000-0000-7000-8000-000000000002', 'Calças', 'calcas',
   'Calças jeans, sarja e moletom para o dia a dia.',
   NULL, NULL, true, now(), now()),
  ('01920000-0000-7000-8000-000000000003', 'Moletons', 'moletons',
   'Moletons com e sem capuz, para os dias frios.',
   NULL, NULL, true, now(), now()),
  ('01920000-0000-7000-8000-000000000004', 'Acessórios', 'acessorios',
   'Bonés, meias e outros acessórios.',
   NULL, NULL, true, now(), now()),
  ('01920000-0000-7000-8000-000000000005', 'Coleção Antiga', 'colecao-antiga',
   'Categoria desativada, não deve aparecer na loja.',
   NULL, NULL, false, now(), now());

INSERT INTO public.seedversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Seeds.Development.S0001__example_categories.sql', now() AT TIME ZONE 'utc');

COMMIT;
