-- Fase 2B.3 — upload de imagens (RF-061, arquivo 20 D06). Fotos antigas (seed) ficam sem storage_key: só a linha sai ao remover.
ALTER TABLE catalog.product_images
    ADD COLUMN storage_key text NULL,                        -- chave no bucket, ex.: products/{productId}/{imageId}.webp
    ADD COLUMN width       int  NULL CHECK (width > 0),
    ADD COLUMN height      int  NULL CHECK (height > 0),
    ADD COLUMN created_at  timestamptz NOT NULL DEFAULT now();
