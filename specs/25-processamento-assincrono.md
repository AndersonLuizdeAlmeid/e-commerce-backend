# 25 — Processamento Assíncrono (Outbox e Jobs)

**Status:** Rascunho

Com e-mails, webhooks de pagamento, revalidação de páginas (SEO) e cancelamento automático, a aplicação passa a precisar de trabalho em segundo plano confiável.

## 1. Outbox pattern

- Tabela `messaging.outbox_messages` gravada **na mesma transação** do caso de uso.
- Um `BackgroundService` (`OutboxProcessor`) lê lotes com `SELECT ... FOR UPDATE SKIP LOCKED` (seguro com várias instâncias), despacha para o handler e marca `processed_at`.
- Retry com backoff exponencial; após N falhas → `failed_at` + alerta.
- Handlers **idempotentes** (podem ser executados mais de uma vez).
- Usos: envio de e-mails (arquivo 23), revalidação do frontend (arquivo 24), estornos (arquivo 21), notificações ao admin.
- *Implementado na Fase 2A* (antecipado da Fase 4): processador com lote de 20, polling de 5 s, backoff 30 s · 2^(n-1) e `failed_at` na 5ª falha. Dados sensíveis do payload (tokens dos links) ficam numa chave `secrets`, apagada (`payload - 'secrets'`) quando a mensagem é processada.

```sql
CREATE TABLE messaging.outbox_messages (
    id            uuid PRIMARY KEY,
    type          text NOT NULL,
    payload       jsonb NOT NULL,
    occurred_at   timestamptz NOT NULL,
    attempts      int NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL,
    processed_at  timestamptz NULL,
    failed_at     timestamptz NULL,
    last_error    text NULL
);
CREATE INDEX ix_outbox_pending ON messaging.outbox_messages(next_attempt_at) WHERE processed_at IS NULL AND failed_at IS NULL;
```

## 2. Jobs agendados

| Job | Frequência | O que faz |
|-----|-----------|-----------|
| **Cancelar pedidos não pagos** (RF-046) | a cada 5 min | Pedidos `AguardandoPagamento` com `placed_at` + **48 h** < agora → `Order.Cancel(reason: PaymentTimeout)`, devolve estoque, cancela pagamento pendente na plataforma, e-mail EM-10. |
| Lembrete de pagamento | a cada 15 min | Pedidos com 24 h sem pagamento e lembrete não enviado → EM-05. |
| Reconciliação de pagamentos | a cada 15 min | Consulta na plataforma pagamentos `Pending` sem webhook há > 10 min. |
| Limpeza de tokens | diário | Refresh tokens e tokens de senha expirados. |
| Alerta de estoque baixo | diário | EM-11 para o admin. |
| Rastreio de envios (evolução) | a cada 2 h | Atualiza status de entrega pelo rastreio dos Correios. |

- Implementação: `BackgroundService` + `PeriodicTimer`; cada execução pega trabalho com `FOR UPDATE SKIP LOCKED` ou **advisory lock** (`pg_try_advisory_lock`) para não rodar em duplicidade com várias instâncias.
- Alternativa se a quantidade de jobs crescer: **Hangfire** com storage PostgreSQL (painel de jobs, retries) — avaliar na Fase 6.
- Corrida pagamento × cancelamento: o cancelamento usa `UPDATE ... WHERE status = 'PendingPayment'` (concorrência otimista); se o webhook de aprovação chegar depois do cancelamento → estorno automático (RN-101, arquivo 21).

## 3. Observabilidade

- Métricas: mensagens pendentes no outbox, idade da mais antiga, falhas por tipo, execuções de job e duração.
- Alerta: mensagem pendente há mais de 15 min ou qualquer `failed_at`.

## 4. Testes

- Processador do outbox com Postgres real (Testcontainers): processa uma vez, retry em falha, duas instâncias não processam a mesma mensagem.
- Job de 48 h com `FakeTimeProvider`.
