-- Fase 2B — catálogo de exemplo da MO (semijoias e bolsas); inativa os exemplos de roupa (SOMENTE Development)
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0009__catalog_category_hierarchy_and_labels.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0009__catalog_category_hierarchy_and_labels.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.seedversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Seeds.Development.S0003__example_jewelry.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'S0003__example_jewelry.sql';
    END IF;
END
$$;

-- ======================================================================
-- S0003__example_jewelry.sql
-- ======================================================================
-- Catálogo de exemplo da MO Acessórios (semijoias e bolsas) — somente em Development (arquivo 05 §5).
-- Inativa os exemplos de roupa das fases anteriores. Fotos ilustrativas (arquivo 20, D12) até o upload da 2B.3.

UPDATE catalog.products SET is_active = false, updated_at = now() WHERE id::text LIKE '01920000-0000-7000-8003-%';
UPDATE catalog.categories SET is_active = false, updated_at = now() WHERE id::text LIKE '01920000-0000-7000-8000-%';

INSERT INTO catalog.categories (id, name, slug, description, color_label, size_label, is_active, created_at, updated_at) VALUES
  ('01930000-0000-7000-8000-000000000001', 'Anéis', 'aneis',
   'Solitários, aparadores e anéis masculinos em banho de ouro, ródio e prata 925.', 'Acabamento', 'Aro', true, now(), now()),
  ('01930000-0000-7000-8000-000000000002', 'Colares', 'colares',
   'Pontos de luz, correntes e iniciais, em vários comprimentos.', 'Acabamento', 'Comprimento', true, now(), now()),
  ('01930000-0000-7000-8000-000000000003', 'Pulseiras', 'pulseiras',
   'Rivieras, elos e pulseiras masculinas.', 'Acabamento', NULL, true, now(), now()),
  ('01930000-0000-7000-8000-000000000004', 'Brincos', 'brincos',
   'Argolas, pontos de luz e gotas para o dia a dia e para festa.', 'Acabamento', NULL, true, now(), now()),
  ('01930000-0000-7000-8000-000000000005', 'Bolsas', 'bolsas',
   'Bolsas de couro e matelassê para completar o look.', 'Cor', 'Tamanho', true, now(), now());

INSERT INTO catalog.colors (id, name, slug, hex) VALUES
  ('01930000-0000-7000-8001-000000000001', 'Dourado',   'dourado',   '#C9A55C'),
  ('01930000-0000-7000-8001-000000000002', 'Prateado',  'prateado',  '#C3C6C8'),
  ('01930000-0000-7000-8001-000000000003', 'Ródio',     'rodio',     '#DADDE0'),
  ('01930000-0000-7000-8001-000000000004', 'Caramelo',  'caramelo',  '#A0673A');

INSERT INTO catalog.sizes (id, name, sort_order) VALUES
  ('01930000-0000-7000-8002-000000000014', '14',    214),
  ('01930000-0000-7000-8002-000000000016', '16',    216),
  ('01930000-0000-7000-8002-000000000018', '18',    218),
  ('01930000-0000-7000-8002-000000000020', '20',    220),
  ('01930000-0000-7000-8002-000000000022', '22',    222),
  ('01930000-0000-7000-8002-000000000140', '40 cm', 340),
  ('01930000-0000-7000-8002-000000000145', '45 cm', 345),
  ('01930000-0000-7000-8002-000000000150', '50 cm', 350),
  ('01930000-0000-7000-8002-000000000160', '60 cm', 360);

INSERT INTO catalog.products
  (id, name, slug, description, category_id, base_price, weight_grams, height_cm, width_cm, length_cm, is_active, created_at, updated_at)
VALUES
  ('01930000-0000-7000-8003-000000000001', 'Anel Solitário Zircônia', 'anel-solitario-zirconia',
   'Solitário com zircônia de 6 mm em garras finas. Banho de ouro 18k ou ródio branco.',
   '01930000-0000-7000-8000-000000000001', 89.90, 20, 3, 5, 5, true, now() - interval '1 day', now() - interval '1 day'),
  ('01930000-0000-7000-8003-000000000002', 'Anel Aparador Cravejado', 'anel-aparador-cravejado',
   'Aro fino cravejado para usar sozinho ou junto do solitário.',
   '01930000-0000-7000-8000-000000000001', 69.90, 20, 3, 5, 5, true, now() - interval '2 days', now() - interval '2 days'),
  ('01930000-0000-7000-8003-000000000003', 'Anel Masculino Liso Prata 925', 'anel-masculino-liso-prata-925',
   'Anel liso de prata 925 maciça, acabamento escovado.',
   '01930000-0000-7000-8000-000000000001', 159.90, 30, 3, 5, 5, true, now() - interval '3 days', now() - interval '3 days'),
  ('01930000-0000-7000-8003-000000000004', 'Colar Ponto de Luz', 'colar-ponto-de-luz',
   'Corrente veneziana com ponto de luz em zircônia.',
   '01930000-0000-7000-8000-000000000002', 79.90, 30, 3, 8, 8, true, now() - interval '4 days', now() - interval '4 days'),
  ('01930000-0000-7000-8003-000000000005', 'Colar Corrente Cartier', 'colar-corrente-cartier',
   'Corrente cartier de elos médios, fecho lagosta.',
   '01930000-0000-7000-8000-000000000002', 119.90, 40, 3, 8, 8, true, now() - interval '5 days', now() - interval '5 days'),
  ('01930000-0000-7000-8003-000000000006', 'Colar Letra Inicial', 'colar-letra-inicial',
   'Pingente de letra com corrente regulável. Escolha a inicial no WhatsApp.',
   '01930000-0000-7000-8000-000000000002', 99.90, 30, 3, 8, 8, true, now() - interval '6 days', now() - interval '6 days'),
  ('01930000-0000-7000-8003-000000000007', 'Pulseira Riviera', 'pulseira-riviera',
   'Riviera de zircônias quadradas, 18 cm com extensor.',
   '01930000-0000-7000-8000-000000000003', 129.90, 30, 3, 8, 8, true, now() - interval '7 days', now() - interval '7 days'),
  ('01930000-0000-7000-8003-000000000008', 'Pulseira Masculina Grumet', 'pulseira-masculina-grumet',
   'Elos grumet de prata 925, 21 cm.',
   '01930000-0000-7000-8000-000000000003', 189.90, 50, 3, 8, 8, true, now() - interval '8 days', now() - interval '8 days'),
  ('01930000-0000-7000-8003-000000000009', 'Brinco Argola Média', 'brinco-argola-media',
   'Argola de 3 cm, leve, fecho clic.',
   '01930000-0000-7000-8000-000000000004', 59.90, 15, 3, 5, 5, true, now() - interval '9 days', now() - interval '9 days'),
  ('01930000-0000-7000-8003-000000000010', 'Brinco Gota Cristal', 'brinco-gota-cristal',
   'Gota de cristal facetado com base cravejada.',
   '01930000-0000-7000-8000-000000000004', 74.90, 15, 3, 5, 5, true, now() - interval '10 days', now() - interval '10 days'),
  ('01930000-0000-7000-8003-000000000011', 'Bolsa Tiracolo Matelassê', 'bolsa-tiracolo-matelasse',
   'Tiracolo matelassê com corrente dourada e alça de couro.',
   '01930000-0000-7000-8000-000000000005', 249.90, 450, 8, 22, 28, true, now() - interval '11 days', now() - interval '11 days'),
  ('01930000-0000-7000-8003-000000000012', 'Bolsa Shopper Couro', 'bolsa-shopper-couro',
   'Shopper de couro legítimo com bolso interno.',
   '01930000-0000-7000-8000-000000000005', 289.90, 700, 10, 30, 35, true, now() - interval '12 days', now() - interval '12 days');

-- A grade: cada produto × acabamentos × tamanhos (lista vazia = sem tamanho).
INSERT INTO catalog.product_variants (id, product_id, sku, color_id, size_id, created_at, updated_at)
SELECT gen_random_uuid(), p.id,
       upper(left(p.slug, 30) || '-' || c.slug || coalesce('-' || regexp_replace(lower(s.name), '[^a-z0-9]+', '', 'g'), '')),
       c.id, s.id, now(), now()
  FROM (VALUES
    ('anel-solitario-zirconia',       ARRAY['Dourado', 'Prateado'],          ARRAY['14', '16', '18', '20']),
    ('anel-aparador-cravejado',       ARRAY['Dourado', 'Ródio'],             ARRAY['14', '16', '18']),
    ('anel-masculino-liso-prata-925', ARRAY['Prateado'],                     ARRAY['18', '20', '22']),
    ('colar-ponto-de-luz',            ARRAY['Dourado', 'Prateado'],          ARRAY['40 cm', '45 cm']),
    ('colar-corrente-cartier',        ARRAY['Dourado'],                      ARRAY['45 cm', '50 cm', '60 cm']),
    ('colar-letra-inicial',           ARRAY['Dourado', 'Prateado', 'Ródio'], ARRAY['40 cm', '45 cm']),
    ('pulseira-riviera',              ARRAY['Dourado', 'Prateado'],          ARRAY[]::text[]),
    ('pulseira-masculina-grumet',     ARRAY['Prateado'],                     ARRAY[]::text[]),
    ('brinco-argola-media',           ARRAY['Dourado', 'Prateado', 'Ródio'], ARRAY[]::text[]),
    ('brinco-gota-cristal',           ARRAY['Dourado'],                      ARRAY[]::text[]),
    ('bolsa-tiracolo-matelasse',      ARRAY['Preto', 'Caramelo'],            ARRAY[]::text[]),
    ('bolsa-shopper-couro',           ARRAY['Caramelo'],                     ARRAY[]::text[])
  ) AS grid(slug, colors, sizes)
  JOIN catalog.products p ON p.slug = grid.slug
  CROSS JOIN LATERAL unnest(grid.colors) AS color_name(name)
  JOIN catalog.colors c ON c.name = color_name.name
  LEFT JOIN LATERAL unnest(grid.sizes) AS size_name(name) ON true
  LEFT JOIN catalog.sizes s ON s.name = size_name.name;

-- Saldo: tudo disponível, menos uma combinação em cada grupo, para mostrar o estado "esgotado".
INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
SELECT v.id,
       CASE WHEN v.sku IN ('ANEL-SOLITARIO-ZIRCONIA-PRATEADO-14', 'ANEL-MASCULINO-LISO-PRATA-925-PRATEADO-22',
                           'COLAR-CORRENTE-CARTIER-DOURADO-60CM', 'PULSEIRA-RIVIERA-PRATEADO', 'BOLSA-TIRACOLO-MATELASSE-CARAMELO')
            THEN 0 ELSE 6 END,
       now()
  FROM catalog.product_variants v
  JOIN catalog.products p ON p.id = v.product_id
 WHERE p.id::text LIKE '01930000-0000-7000-8003-%';

-- Uma foto ilustrativa por acabamento.
INSERT INTO catalog.product_images (id, product_id, color_id, url, alt, position)
SELECT DISTINCT ON (p.id, c.id)
       gen_random_uuid(), p.id, c.id,
       'https://placehold.co/800x1000/e9dfca/6b5320/png?text=' || replace(p.name || ' ' || c.name, ' ', '%20'),
       p.name || ', acabamento ' || lower(c.name),
       0
  FROM catalog.product_variants v
  JOIN catalog.products p ON p.id = v.product_id
  JOIN catalog.colors c ON c.id = v.color_id
 WHERE p.id::text LIKE '01930000-0000-7000-8003-%'
 ORDER BY p.id, c.id;

INSERT INTO public.seedversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Seeds.Development.S0003__example_jewelry.sql', now() AT TIME ZONE 'utc');

COMMIT;
