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
