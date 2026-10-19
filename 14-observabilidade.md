# 14 — Observabilidade

**Status:** Em revisão

## 1. Logs

- **Serilog** com saída **JSON estruturada** (console → coletado pelo ambiente). Em dev, console legível.
- Enrichers: `CorrelationId`, `TraceId/SpanId`, `UserId` (quando autenticado), `Environment`, `MachineName`.
- `UseSerilogRequestLogging()` — uma linha por request com método, rota, status, duração.
- Níveis: `Information` por padrão; `Microsoft.AspNetCore` em `Warning`.
- **Proibido logar**: senhas, tokens, hash de senha, CPF completo, corpo de requisições de auth. Mascarar e-mail (`a***@exemplo.com`) e IP quando possível.

## 2. Correlation ID

- Middleware lê `X-Correlation-Id` do request ou gera um novo; devolve no response e adiciona ao `LogContext`.
- Presente nos ProblemDetails (`traceId`).

## 3. Tracing e métricas — OpenTelemetry

- Instrumentações: ASP.NET Core, HttpClient, **Npgsql** (spans das queries Dapper), runtime.
- Exportador **OTLP** configurável (Jaeger/Tempo/Grafana/Azure Monitor/Datadog — ver arquivo 20). Em dev: Aspire Dashboard via Docker.
- Métricas de negócio (`System.Diagnostics.Metrics`):
  | Métrica | Tipo |
  |---------|------|
  | `ecommerce.orders.placed` | Counter (tag: status) |
  | `ecommerce.orders.revenue` | Counter (BRL) |
  | `ecommerce.checkout.failed` | Counter (tag: reason) |
  | `ecommerce.auth.login.failed` | Counter |
  | `ecommerce.ratelimit.rejected` | Counter (tag: policy) |
  | `ecommerce.cache.hit` / `.miss` | Counter (tag: key prefix) |
  | `ecommerce.payments.created` / `.approved` / `.rejected` | Counter (tag: method) |
  | `ecommerce.webhooks.received` / `.invalid_signature` | Counter (tag: provider) |
  | `ecommerce.orders.auto_cancelled` | Counter |
  | `ecommerce.shipping.quote.duration` / `.fallback_used` | Histogram / Counter |
  | `ecommerce.emails.sent` / `.failed` | Counter (tag: template) |
  | `ecommerce.outbox.pending` / `.oldest_age_seconds` | Gauge |

## 4. Health checks

| Endpoint | Verifica | Uso |
|----------|----------|-----|
| `/health/live` | Processo responde (sem dependências) | liveness probe |
| `/health/ready` | PostgreSQL (`SELECT 1`), Redis (se habilitado), migrations aplicadas | readiness probe / load balancer |

- Respostas em JSON; sem rate limit; sem autenticação, mas `/health/ready` com detalhes somente para rede interna.

## 5. Auditoria

- Ações administrativas (mudança de preço, estoque, status de pedido, role de usuário) geram registro com **quem, o quê, quando** — via tabelas de histórico de domínio (`order_status_history`, `stock_movements`) e log estruturado `Audit`.
- Evolução: tabela `audit.audit_log` genérica.

## 6. Alertas (quando em produção)

- Taxa de 5xx > 1% em 5 min; p95 > 1 s; readiness falhando; pico de `login.failed`/`ratelimit.rejected`.
- Webhook com assinatura inválida; nenhum webhook recebido há X horas com pagamentos pendentes; estorno com falha; outbox atrasado; uso frequente do frete de contingência.
