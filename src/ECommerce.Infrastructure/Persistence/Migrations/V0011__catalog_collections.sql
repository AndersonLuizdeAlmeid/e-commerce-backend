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
