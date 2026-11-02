-- Fase 2B.4 — coleções, peça em destaque e ocasiões
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0010__catalog_images_storage.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0010__catalog_images_storage.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0011__catalog_collections.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0011__catalog_collections.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0012__catalog_showcase.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0012__catalog_showcase.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0011__catalog_collections.sql
-- ======================================================================
-- Fase 2B.4 — coleções (RF-024, arquivo 26 ADR-F08): atravessam as categorias; o produto mora em uma categoria e pode estar em várias coleções.
CREATE TABLE catalog.collections (
    id               uuid PRIMARY KEY,
    name             text NOT NULL UNIQUE,
    slug             text NOT NULL UNIQUE,
    description      text NOT NULL DEFAULT '',
    seo_title        text NULL,
    seo_description  text NULL,
    image_url        text NULL,                            -- capa opcional (RF-061)
    image_key        text NULL,                            -- chave no bucket, ex.: collections/{id}/{imagem}.webp
    card_theme       text NOT NULL DEFAULT 'Ivory' CHECK (card_theme IN ('Ivory', 'Graphite')),  -- arquivo 26 §2.2
    position         int  NOT NULL DEFAULT 0,              -- ordem na navegação
    is_active        boolean NOT NULL DEFAULT true,
    version          int  NOT NULL DEFAULT 0,              -- concorrência otimista (arquivo 06 §6.1)
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);

CREATE INDEX ix_collections_active_position ON catalog.collections(position, name) WHERE is_active;

-- RF-024 CA6: excluir a coleção só remove os vínculos.
CREATE TABLE catalog.product_collections (
    collection_id  uuid NOT NULL REFERENCES catalog.collections(id) ON DELETE CASCADE,
    product_id     uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
    added_at       timestamptz NOT NULL,
    PRIMARY KEY (collection_id, product_id)
);

CREATE INDEX ix_product_collections_product ON catalog.product_collections(product_id);

-- RF-024 CA4: slug antigo de coleção também responde 301.
ALTER TABLE catalog.slug_history DROP CONSTRAINT slug_history_entity_type_check;
ALTER TABLE catalog.slug_history
    ADD CONSTRAINT slug_history_entity_type_check CHECK (entity_type IN ('Product', 'Category', 'Collection'));

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0011__catalog_collections.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0012__catalog_showcase.sql
-- ======================================================================
-- Fase 2B.4 — vitrine (RF-025, arquivo 26 D-F03): peça em destaque da home e ocasião da vez.

-- Linha única: sem linha, a home mostra a novidade mais recente com estoque (RF-025 CA1).
CREATE TABLE catalog.showcase_featured (
    id          boolean PRIMARY KEY DEFAULT true CHECK (id),
    product_id  uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
    color_id    uuid NULL REFERENCES catalog.colors(id) ON DELETE SET NULL,
    updated_at  timestamptz NOT NULL
);

CREATE TABLE catalog.occasions (
    id          uuid PRIMARY KEY,
    name        text NOT NULL,
    short_text  text NOT NULL DEFAULT '' CHECK (char_length(short_text) <= 160),
    image_url   text NULL,
    image_key   text NULL,
    starts_on   date NOT NULL,                             -- início e fim inclusivos, no horário de Brasília (RF-025 CA3)
    ends_on     date NOT NULL,
    is_active   boolean NOT NULL DEFAULT true,
    version     int  NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL,
    CONSTRAINT ck_occasions_period CHECK (ends_on >= starts_on),
    -- RF-025 CA2: a API recusa antes com 409; o banco garante mesmo com escrita simultânea ou script manual.
    CONSTRAINT ex_occasions_active_period EXCLUDE USING gist (daterange(starts_on, ends_on, '[]') WITH &&) WHERE (is_active)
);

CREATE TABLE catalog.occasion_products (
    occasion_id  uuid NOT NULL REFERENCES catalog.occasions(id) ON DELETE CASCADE,
    product_id   uuid NOT NULL REFERENCES catalog.products(id) ON DELETE CASCADE,
    position     smallint NOT NULL,
    PRIMARY KEY (occasion_id, product_id)
);

CREATE INDEX ix_occasion_products_product ON catalog.occasion_products(product_id);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0012__catalog_showcase.sql', now() AT TIME ZONE 'utc');

COMMIT;
