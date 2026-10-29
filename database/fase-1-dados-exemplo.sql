-- Fase 1 — produtos de exemplo com grade, estoque e imagens (SOMENTE Development; requer fase-0-dados-exemplo)
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0005__seed_colors_and_sizes.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0005__seed_colors_and_sizes.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.seedversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Seeds.Development.S0002__example_products.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'S0002__example_products.sql';
    END IF;
END
$$;

-- ======================================================================
-- S0002__example_products.sql
-- ======================================================================
-- Produtos de exemplo com grade de variações, estoque e imagens — somente em Development (arquivo 05 §5).
-- Depende de S0001 (categorias) e de V0005 (cores e tamanhos).

INSERT INTO catalog.products
  (id, name, slug, description, category_id, base_price, weight_grams, height_cm, width_cm, length_cm,
   seo_title, seo_description, is_active, created_at, updated_at)
VALUES
  ('01920000-0000-7000-8003-000000000001', 'Camiseta Básica', 'camiseta-basica',
   'Camiseta de algodão penteado 30.1, gola careca e modelagem reta. Vai bem com tudo.',
   '01920000-0000-7000-8000-000000000001', 59.90, 180, 2, 25, 30,
   'Camiseta Básica de Algodão', 'Camiseta básica de algodão penteado em 4 cores, do PP ao XG.',
   true, now() - interval '30 days', now() - interval '2 days'),
  ('01920000-0000-7000-8003-000000000002', 'Camiseta Estampada Onda', 'camiseta-estampada-onda',
   'Camiseta de algodão com estampa de onda em serigrafia à base d''água.',
   '01920000-0000-7000-8000-000000000001', 79.90, 190, 2, 25, 30,
   NULL, NULL, true, now() - interval '5 days', now() - interval '5 days'),
  ('01920000-0000-7000-8003-000000000003', 'Calça Jeans Reta', 'calca-jeans-reta',
   'Calça jeans de algodão com elastano, cintura média e corte reto.',
   '01920000-0000-7000-8000-000000000002', 189.90, 650, 4, 30, 40,
   NULL, NULL, true, now() - interval '20 days', now() - interval '20 days'),
  ('01920000-0000-7000-8003-000000000004', 'Calça Sarja Chino', 'calca-sarja-chino',
   'Calça chino em sarja leve, com bolsos faca e barra italiana.',
   '01920000-0000-7000-8000-000000000002', 159.90, 520, 4, 30, 40,
   NULL, NULL, true, now() - interval '12 days', now() - interval '12 days'),
  ('01920000-0000-7000-8003-000000000005', 'Moletom Canguru', 'moletom-canguru',
   'Moletom flanelado com capuz e bolso canguru. Quentinho por dentro.',
   '01920000-0000-7000-8000-000000000003', 199.90, 700, 6, 30, 40,
   'Moletom Canguru com Capuz', NULL, true, now() - interval '8 days', now() - interval '8 days'),
  ('01920000-0000-7000-8003-000000000006', 'Moletom Careca', 'moletom-careca',
   'Moletom gola careca, sem capuz, com punhos e barra canelados.',
   '01920000-0000-7000-8000-000000000003', 169.90, 620, 6, 30, 40,
   NULL, NULL, true, now() - interval '3 days', now() - interval '3 days'),
  ('01920000-0000-7000-8003-000000000007', 'Boné Aba Curva', 'bone-aba-curva',
   'Boné de sarja com aba curva e regulagem traseira.',
   '01920000-0000-7000-8000-000000000004', 69.90, 120, 12, 20, 25,
   NULL, NULL, true, now() - interval '15 days', now() - interval '15 days'),
  ('01920000-0000-7000-8003-000000000008', 'Kit 3 Meias Cano Médio', 'kit-3-meias-cano-medio',
   'Kit com 3 pares de meias de algodão, cano médio, tamanho único (39 a 43).',
   '01920000-0000-7000-8000-000000000004', 39.90, 150, 3, 12, 20,
   NULL, NULL, true, now() - interval '1 day', now() - interval '1 day'),
  -- inativo: a página responde redirect permanente para a categoria (SEO-02)
  ('01920000-0000-7000-8003-000000000009', 'Camiseta Coleção 2024', 'camiseta-colecao-2024',
   'Produto fora de linha.',
   '01920000-0000-7000-8000-000000000001', 49.90, 180, 2, 25, 30,
   NULL, NULL, false, now() - interval '400 days', now() - interval '60 days');

-- Grade cor × tamanho (RF-056). SKU = prefixo-COR-TAMANHO.
INSERT INTO catalog.product_variants (id, product_id, sku, color_id, size_id, price, is_active, created_at, updated_at)
SELECT gen_random_uuid(), g.product_id,
       g.sku_prefix || '-' || upper(c.slug) || '-' || upper(translate(s.name, 'Ú', 'U')),
       c.id, s.id, NULL, true, now(), now()
  FROM (VALUES
          ('01920000-0000-7000-8003-000000000001'::uuid, 'CAM-BAS', ARRAY['preto','branco','azul-marinho','cinza-mescla'], ARRAY['PP','P','M','G','GG','XG']),
          ('01920000-0000-7000-8003-000000000002'::uuid, 'CAM-OND', ARRAY['branco','preto'],                            ARRAY['P','M','G','GG']),
          ('01920000-0000-7000-8003-000000000003'::uuid, 'CAL-JEA', ARRAY['azul-marinho','preto'],                      ARRAY['36','38','40','42','44','46']),
          ('01920000-0000-7000-8003-000000000004'::uuid, 'CAL-CHI', ARRAY['bege','verde-militar','preto'],              ARRAY['38','40','42','44']),
          ('01920000-0000-7000-8003-000000000005'::uuid, 'MOL-CAN', ARRAY['cinza-mescla','preto','verde-militar'],      ARRAY['P','M','G','GG']),
          ('01920000-0000-7000-8003-000000000006'::uuid, 'MOL-CAR', ARRAY['cinza-mescla','azul-marinho'],               ARRAY['P','M','G']),
          ('01920000-0000-7000-8003-000000000007'::uuid, 'BON-CUR', ARRAY['preto','bege'],                              ARRAY['Único']),
          ('01920000-0000-7000-8003-000000000009'::uuid, 'CAM-C24', ARRAY['preto'],                                     ARRAY['M'])
       ) AS g(product_id, sku_prefix, colors, sizes)
  JOIN catalog.colors c ON c.slug = ANY (g.colors)
  JOIN catalog.sizes  s ON s.name = ANY (g.sizes);

-- Produto sem cor nem tamanho: uma variação "única" (RN-022)
INSERT INTO catalog.product_variants (id, product_id, sku, color_id, size_id, price, is_active, created_at, updated_at)
VALUES (gen_random_uuid(), '01920000-0000-7000-8003-000000000008', 'MEI-KIT3', NULL, NULL, NULL, true, now(), now());

-- XG custa mais (preço próprio da variação); uma variação desativada não aparece na loja
UPDATE catalog.product_variants SET price = 64.90 WHERE sku LIKE 'CAM-BAS-%-XG';
UPDATE catalog.product_variants SET is_active = false WHERE sku = 'CAM-OND-PRETO-GG';

-- Códigos de barras EAN-13 válidos (prefixo 789 + sequência + dígito verificador) na Camiseta Básica
UPDATE catalog.product_variants v
   SET barcode = b.base || ((10 - (SELECT sum(substr(b.base, i, 1)::int * CASE WHEN i % 2 = 0 THEN 3 ELSE 1 END)
                                     FROM generate_series(1, 12) AS i) % 10) % 10)::text
  FROM (SELECT id, '789' || lpad((row_number() OVER (ORDER BY sku))::text, 9, '0') AS base
          FROM catalog.product_variants
         WHERE product_id = '01920000-0000-7000-8003-000000000001') AS b
 WHERE v.id = b.id;

-- Estoque: quantidade pseudoaleatória estável por SKU, com algumas combinações esgotadas
INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
SELECT id, abs(hashtext(sku)) % 15, now()
  FROM catalog.product_variants;

UPDATE inventory.stock_items s
   SET quantity_on_hand = 0
  FROM catalog.product_variants v
 WHERE v.id = s.variant_id
   AND v.sku IN ('CAM-BAS-AZUL-MARINHO-PP', 'CAM-BAS-PRETO-XG', 'CAL-JEA-PRETO-46', 'MOL-CAN-VERDE-MILITAR-GG');

-- Imagens: duas por cor (frente e costas); o kit de meias tem galeria sem cor
INSERT INTO catalog.product_images (id, product_id, color_id, url, alt, position)
SELECT gen_random_uuid(), pc.product_id, c.id,
       'https://placehold.co/800x1000/' || lower(ltrim(c.hex, '#')) || '/'
         || CASE WHEN c.slug IN ('branco', 'bege') THEN '151515' ELSE 'ffffff' END
         || '/png?text=' || p.slug || '-' || pos,
       p.name || ' ' || lower(c.name) || CASE pos WHEN 1 THEN ', frente' ELSE ', costas' END,
       pos
  FROM (SELECT DISTINCT product_id, color_id FROM catalog.product_variants WHERE color_id IS NOT NULL) AS pc
  JOIN catalog.products p ON p.id = pc.product_id
  JOIN catalog.colors   c ON c.id = pc.color_id
 CROSS JOIN generate_series(1, 2) AS pos;

INSERT INTO catalog.product_images (id, product_id, color_id, url, alt, position)
SELECT gen_random_uuid(), '01920000-0000-7000-8003-000000000008', NULL,
       'https://placehold.co/800x1000/e7e5e4/151515/png?text=kit-3-meias-cano-medio-' || pos,
       'Kit com 3 pares de meias cano médio' || CASE pos WHEN 1 THEN ', vista de frente' ELSE ', detalhe do tecido' END,
       pos
  FROM generate_series(1, 2) AS pos;

-- Slug antigo da Camiseta Básica → redirect 301 (SEO-02)
INSERT INTO catalog.slug_history (old_slug, entity_type, entity_id, changed_at)
VALUES ('camiseta-basica-algodao', 'Product', '01920000-0000-7000-8003-000000000001', now() - interval '10 days');

INSERT INTO public.seedversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Seeds.Development.S0002__example_products.sql', now() AT TIME ZONE 'utc');

COMMIT;
