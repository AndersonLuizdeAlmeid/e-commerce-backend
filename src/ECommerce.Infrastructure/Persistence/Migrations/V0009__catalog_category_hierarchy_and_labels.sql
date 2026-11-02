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
