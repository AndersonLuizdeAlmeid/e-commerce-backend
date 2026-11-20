-- Fase 6.3 — devolução de pedido entregue e exclusão de conta (LGPD)
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0021__outbox_lease_and_ops.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0021__outbox_lease_and_ops.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0022__returns_and_account_removal.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0022__returns_and_account_removal.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0022__returns_and_account_removal.sql
-- ======================================================================
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

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0022__returns_and_account_removal.sql', now() AT TIME ZONE 'utc');

COMMIT;
