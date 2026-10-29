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
