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
