-- Fase 0 — extensões, schema catalog e categorias
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
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0001__extensions_and_schemas.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0001__extensions_and_schemas.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0002__catalog_categories.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0002__catalog_categories.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0001__extensions_and_schemas.sql
-- ======================================================================
-- Extensões (arquivo 05 §1)
CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;

-- unaccent() não é IMMUTABLE; wrapper para uso em índices
CREATE FUNCTION public.immutable_unaccent(text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
  AS $$ SELECT public.unaccent('public.unaccent', $1) $$;

-- Schemas são criados pela migration do contexto que os usa.
CREATE SCHEMA catalog;

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0001__extensions_and_schemas.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0002__catalog_categories.sql
-- ======================================================================
CREATE TABLE catalog.categories (
    id               uuid PRIMARY KEY,
    name             text NOT NULL UNIQUE,
    slug             text NOT NULL UNIQUE,
    description      text NOT NULL DEFAULT '',     -- texto indexável (SEO-10)
    seo_title        text NULL,
    seo_description  text NULL,
    is_active        boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0002__catalog_categories.sql', now() AT TIME ZONE 'utc');

COMMIT;
