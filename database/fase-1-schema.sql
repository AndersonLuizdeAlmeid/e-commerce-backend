-- Fase 1 — vitrine: cores, tamanhos, produtos, variações, imagens, slugs, saldo de estoque
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0002__catalog_categories.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0002__catalog_categories.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0003__catalog_products_and_variants.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0003__catalog_products_and_variants.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0004__inventory_stock_items.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0004__inventory_stock_items.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0005__seed_colors_and_sizes.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0005__seed_colors_and_sizes.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0003__catalog_products_and_variants.sql
-- ======================================================================
-- Fase 1 — vitrine: cores, tamanhos, produtos, grade de variações, imagens e histórico de slugs (arquivo 05 §3)

CREATE TABLE catalog.colors (
    id    uuid PRIMARY KEY,
    name  text NOT NULL UNIQUE,                 -- "Azul Marinho"
    slug  text NOT NULL UNIQUE,                 -- usado nos filtros da loja (?cor=azul-marinho)
    hex   char(7) NOT NULL CHECK (hex ~ '^#[0-9A-Fa-f]{6}$')
);

CREATE TABLE catalog.sizes (
    id          uuid PRIMARY KEY,
    name        text NOT NULL UNIQUE,           -- "M", "38"
    sort_order  smallint NOT NULL
);

CREATE TABLE catalog.products (
    id               uuid PRIMARY KEY,
    name             text NOT NULL,
    slug             text NOT NULL UNIQUE,
    description      text NOT NULL DEFAULT '',
    category_id      uuid NOT NULL REFERENCES catalog.categories(id),
    base_price       numeric(12,2) NOT NULL CHECK (base_price > 0),
    weight_grams     int NOT NULL CHECK (weight_grams > 0),
    height_cm        numeric(6,1) NOT NULL,
    width_cm         numeric(6,1) NOT NULL,
    length_cm        numeric(6,1) NOT NULL,
    seo_title        text NULL,
    seo_description  text NULL,
    is_active        boolean NOT NULL DEFAULT true,
    version          int NOT NULL DEFAULT 0,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);
CREATE INDEX ix_products_category  ON catalog.products(category_id) WHERE is_active;
CREATE INDEX ix_products_name_trgm ON catalog.products USING gin (public.immutable_unaccent(lower(name)) gin_trgm_ops);

CREATE TABLE catalog.product_variants (
    id            uuid PRIMARY KEY,
    product_id    uuid NOT NULL REFERENCES catalog.products(id),
    sku           text NOT NULL UNIQUE,
    barcode       varchar(14) NULL UNIQUE,                  -- EAN/GTIN (RF-059)
    color_id      uuid NULL REFERENCES catalog.colors(id),
    size_id       uuid NULL REFERENCES catalog.sizes(id),
    price         numeric(12,2) NULL CHECK (price > 0),    -- NULL = base_price do produto
    weight_grams  int NULL,
    height_cm     numeric(6,1) NULL,
    width_cm      numeric(6,1) NULL,
    length_cm     numeric(6,1) NULL,
    is_active     boolean NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
-- RN-021: uma combinação cor+tamanho por produto ("sem cor"/"sem tamanho" contam como valor)
CREATE UNIQUE INDEX ux_variants_combination ON catalog.product_variants(product_id, color_id, size_id) NULLS NOT DISTINCT;
CREATE INDEX ix_variants_product ON catalog.product_variants(product_id) WHERE is_active;
CREATE INDEX ix_variants_color   ON catalog.product_variants(color_id) WHERE is_active;
CREATE INDEX ix_variants_size    ON catalog.product_variants(size_id)  WHERE is_active;

CREATE TABLE catalog.product_images (
    id          uuid PRIMARY KEY,
    product_id  uuid NOT NULL REFERENCES catalog.products(id),
    color_id    uuid NULL REFERENCES catalog.colors(id),    -- galeria por cor; NULL = vale para todas
    url         text NOT NULL,
    alt         text NOT NULL,
    position    smallint NOT NULL,
    CONSTRAINT ux_product_images_position UNIQUE NULLS NOT DISTINCT (product_id, color_id, position)
);
CREATE INDEX ix_product_images_color ON catalog.product_images(color_id);

CREATE TABLE catalog.slug_history (                        -- redirects 301 (SEO-02)
    old_slug     text PRIMARY KEY,
    entity_type  text NOT NULL CHECK (entity_type IN ('Product','Category')),
    entity_id    uuid NOT NULL,
    changed_at   timestamptz NOT NULL
);
CREATE INDEX ix_slug_history_entity ON catalog.slug_history(entity_type, entity_id);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0003__catalog_products_and_variants.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0004__inventory_stock_items.sql
-- ======================================================================
-- Fase 1 — só o saldo por variação, para a vitrine mostrar "disponível sim/não" (RF-021 CA2).
-- As movimentações (inventory.stock_movements) e os ajustes entram com o admin de estoque (Fase 3).
CREATE SCHEMA inventory;

CREATE TABLE inventory.stock_items (
    variant_id           uuid PRIMARY KEY REFERENCES catalog.product_variants(id),
    quantity_on_hand     int NOT NULL CHECK (quantity_on_hand >= 0),
    low_stock_threshold  int NOT NULL DEFAULT 3,
    version              int NOT NULL DEFAULT 0,
    updated_at           timestamptz NOT NULL
);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0004__inventory_stock_items.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0005__seed_colors_and_sizes.sql
-- ======================================================================
-- Cores básicas e tamanhos padrão, em todos os ambientes; editáveis pelo admin depois (arquivo 05 §5).
INSERT INTO catalog.colors (id, name, slug, hex) VALUES
  ('01920000-0000-7000-8001-000000000001', 'Preto',         'preto',         '#151515'),
  ('01920000-0000-7000-8001-000000000002', 'Branco',        'branco',        '#F5F5F5'),
  ('01920000-0000-7000-8001-000000000003', 'Cinza Mescla',  'cinza-mescla',  '#9E9E9E'),
  ('01920000-0000-7000-8001-000000000004', 'Azul Marinho',  'azul-marinho',  '#1B2A4A'),
  ('01920000-0000-7000-8001-000000000005', 'Vermelho',      'vermelho',      '#B3261E'),
  ('01920000-0000-7000-8001-000000000006', 'Verde Militar', 'verde-militar', '#4B5320'),
  ('01920000-0000-7000-8001-000000000007', 'Bege',          'bege',          '#D8C3A5');

INSERT INTO catalog.sizes (id, name, sort_order) VALUES
  ('01920000-0000-7000-8002-000000000001', 'PP',    10),
  ('01920000-0000-7000-8002-000000000002', 'P',     20),
  ('01920000-0000-7000-8002-000000000003', 'M',     30),
  ('01920000-0000-7000-8002-000000000004', 'G',     40),
  ('01920000-0000-7000-8002-000000000005', 'GG',    50),
  ('01920000-0000-7000-8002-000000000006', 'XG',    60),
  ('01920000-0000-7000-8002-000000000034', '34',   134),
  ('01920000-0000-7000-8002-000000000036', '36',   136),
  ('01920000-0000-7000-8002-000000000038', '38',   138),
  ('01920000-0000-7000-8002-000000000040', '40',   140),
  ('01920000-0000-7000-8002-000000000042', '42',   142),
  ('01920000-0000-7000-8002-000000000044', '44',   144),
  ('01920000-0000-7000-8002-000000000046', '46',   146),
  ('01920000-0000-7000-8002-000000000099', 'Único', 999);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0005__seed_colors_and_sizes.sql', now() AT TIME ZONE 'utc');

COMMIT;
