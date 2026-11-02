-- Fase 2B — catálogo no painel: subcategoria, rótulos da grade por categoria e dados das fotos
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0008__messaging_outbox_and_email_log.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0008__messaging_outbox_and_email_log.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0009__catalog_category_hierarchy_and_labels.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0009__catalog_category_hierarchy_and_labels.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0010__catalog_images_storage.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0010__catalog_images_storage.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0009__catalog_category_hierarchy_and_labels.sql
-- ======================================================================
-- Fase 2B.2 — subcategoria (Q07: um nível) e rótulos da grade por categoria (RF-050, arquivo 26 ADR-F07)

ALTER TABLE catalog.categories
    ADD COLUMN parent_id   uuid NULL REFERENCES catalog.categories(id),
    ADD COLUMN color_label text NULL CHECK (char_length(color_label) BETWEEN 1 AND 30),  -- "Acabamento"; NULL herda
    ADD COLUMN size_label  text NULL CHECK (char_length(size_label) BETWEEN 1 AND 30),   -- "Aro"; NULL herda
    ADD COLUMN version     int NOT NULL DEFAULT 0,                                       -- concorrência otimista (arquivo 06 §6.1)
    ADD CONSTRAINT ck_categories_parent_not_self CHECK (parent_id <> id);

CREATE INDEX ix_categories_parent ON catalog.categories(parent_id);

-- Q07: só um nível. O domínio já recusa, mas o banco garante mesmo com script manual (DBeaver).
CREATE FUNCTION catalog.ensure_single_level_category() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.parent_id IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM catalog.categories WHERE id = NEW.parent_id AND parent_id IS NOT NULL) THEN
            RAISE EXCEPTION 'Subcategoria não pode ter subcategoria (um nível só).' USING ERRCODE = 'check_violation';
        END IF;
        IF EXISTS (SELECT 1 FROM catalog.categories WHERE parent_id = NEW.id) THEN
            RAISE EXCEPTION 'Categoria com subcategorias não pode virar subcategoria.' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_categories_single_level
    BEFORE INSERT OR UPDATE OF parent_id ON catalog.categories
    FOR EACH ROW EXECUTE FUNCTION catalog.ensure_single_level_category();

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0009__catalog_category_hierarchy_and_labels.sql', now() AT TIME ZONE 'utc');

-- ======================================================================
-- V0010__catalog_images_storage.sql
-- ======================================================================
-- Fase 2B.3 — upload de imagens (RF-061, arquivo 20 D06). Fotos antigas (seed) ficam sem storage_key: só a linha sai ao remover.
ALTER TABLE catalog.product_images
    ADD COLUMN storage_key text NULL,                        -- chave no bucket, ex.: products/{productId}/{imageId}.webp
    ADD COLUMN width       int  NULL CHECK (width > 0),
    ADD COLUMN height      int  NULL CHECK (height > 0),
    ADD COLUMN created_at  timestamptz NOT NULL DEFAULT now();

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0010__catalog_images_storage.sql', now() AT TIME ZONE 'utc');

COMMIT;
