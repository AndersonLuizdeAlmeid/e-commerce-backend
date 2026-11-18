-- Fase 6.1 — reserva do Outbox e marcas dos jobs de operação (EM-11, EM-14)
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
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0020__backoffice.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0020__backoffice.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0021__outbox_lease_and_ops.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0021__outbox_lease_and_ops.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0021__outbox_lease_and_ops.sql
-- ======================================================================
-- Fase 6.1 — Outbox com reserva e marcas dos jobs de operação (entrega 6 §3, P1, P3 e P4).

-- Reserva (P1): a mensagem é pega numa instrução curta e processada fora de transação. Se a API cair no meio,
-- a reserva vence e outra rodada pega de novo; o token impede que um processamento atrasado marque o resultado
-- por cima de quem pegou depois.
ALTER TABLE messaging.outbox_messages ADD COLUMN locked_until timestamptz NULL;
ALTER TABLE messaging.outbox_messages ADD COLUMN lock_token uuid NULL;

-- Vigia de operação (P3): mensagens que desistiram desde o último alerta.
CREATE INDEX ix_outbox_failed ON messaging.outbox_messages(failed_at) WHERE failed_at IS NOT NULL;

CREATE SCHEMA ops;

-- Última execução de cada tarefa agendada ou alerta: EM-11 uma vez por dia (P4) e no máximo um EM-14 por condição
-- a cada 6 h (P3). Gravar a marca é o que garante que duas instâncias não mandem o mesmo e-mail.
CREATE TABLE ops.job_marks (
    key      text PRIMARY KEY,
    last_at  timestamptz NOT NULL
);

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0021__outbox_lease_and_ops.sql', now() AT TIME ZONE 'utc');

COMMIT;
