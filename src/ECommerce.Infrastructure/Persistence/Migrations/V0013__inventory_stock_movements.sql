-- Fase 3.1 — histórico de movimentações de estoque (RF-052). O saldo anterior não ganha movimentação retroativa.
CREATE TABLE inventory.stock_movements (
    id           uuid PRIMARY KEY,
    variant_id   uuid NOT NULL REFERENCES inventory.stock_items(variant_id),
    type         text NOT NULL CHECK (type IN ('In','Out','Adjustment','OrderPlaced','OrderCancelled')),
    quantity     int  NOT NULL CHECK (quantity <> 0),      -- delta com sinal
    reason       text NULL,
    order_id     uuid NULL,
    performed_by uuid NULL,
    created_at   timestamptz NOT NULL
);
CREATE INDEX ix_stock_movements_variant ON inventory.stock_movements(variant_id, created_at DESC);

-- Toda variação tem saldo: o admin de estoque e a Fase 4 contam com esta linha.
INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
SELECT v.id, 0, now()
  FROM catalog.product_variants v
ON CONFLICT (variant_id) DO NOTHING;
