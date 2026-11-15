-- Fase 5 — pagamento manual e índices do painel (pedidos, estornos, usuários e dashboard)
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0019__payments.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0019__payments.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0020__backoffice.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0020__backoffice.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0020__backoffice.sql
-- ======================================================================
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

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0020__backoffice.sql', now() AT TIME ZONE 'utc');

COMMIT;
