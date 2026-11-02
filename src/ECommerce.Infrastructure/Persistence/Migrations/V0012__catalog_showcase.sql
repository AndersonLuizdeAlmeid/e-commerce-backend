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
