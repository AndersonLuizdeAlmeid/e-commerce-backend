-- Fase 5 — backoffice de pedidos, estornos, usuários e dashboard (RF-053..055).

-- Pagamento confirmado à mão pela loja (RF-053 CA1, entrega 5 §3 P1): não passa pela plataforma.
ALTER TABLE payments.payments DROP CONSTRAINT payments_method_check;
ALTER TABLE payments.payments ADD CONSTRAINT payments_method_check CHECK (method IN ('Pix','CreditCard','Manual'));
ALTER TABLE payments.payments ADD CONSTRAINT payments_manual_provider_check CHECK ((method = 'Manual') = (provider = 'manual'));

-- Dashboard: faturamento pelos pagamentos aprovados no período (entrega 5 §3 P10).
CREATE INDEX ix_payments_approved_at ON payments.payments(approved_at) WHERE approved_at IS NOT NULL;

-- Fila de estornos pendentes (entrega 5 §3 P6).
CREATE INDEX ix_payments_needs_refund ON payments.payments(updated_at) WHERE status IN ('Approved','RefundFailed');

-- Lista de usuários do painel, mais recentes primeiro.
CREATE INDEX ix_users_created ON identity.users(created_at DESC);
