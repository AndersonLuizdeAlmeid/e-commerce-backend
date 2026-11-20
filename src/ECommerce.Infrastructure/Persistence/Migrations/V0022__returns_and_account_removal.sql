-- Fase 6.3 — devolução de pedido entregue e exclusão de conta (entrega 6 §3, P7 e P11).

-- Devolução (P11): Entregue → Devolvido, com o mesmo estorno total do cancelamento. A loja confere a peça e decide o
-- estoque por ajuste; o motivo fica na nota do histórico.
ALTER TABLE ordering.orders DROP CONSTRAINT orders_status_check;
ALTER TABLE ordering.orders ADD CONSTRAINT orders_status_check
    CHECK (status IN ('PendingPayment','PaymentConfirmed','Preparing','Shipped','Delivered','Cancelled','Returned'));

-- Exclusão de conta (P7): a linha fica, porque os pedidos precisam continuar existindo (obrigação fiscal), mas sem
-- nenhum dado pessoal. O e-mail vira removida-<id>@invalid e a conta nunca mais é reativada.
ALTER TABLE identity.users ADD COLUMN anonymized_at timestamptz NULL;
ALTER TABLE identity.users ADD CONSTRAINT users_anonymized_inactive CHECK (anonymized_at IS NULL OR NOT is_active);
