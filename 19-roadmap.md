# 19 — Roadmap

**Status:** Em revisão

Entregas incrementais; cada fase termina com testes verdes, deploy em staging e demonstração ao cliente.

## Fase 0 — Fundação
- Solution, `Directory.*.props`, Docker Compose (Postgres + Mailpit), CI.
- Abstractions do Domain, persistência (DataSource, UnitOfWork, DbUp), **Outbox e infraestrutura de jobs** (arquivo 25).
- Pipeline HTTP: ProblemDetails, correlation id, Serilog, OpenTelemetry, health checks, OpenAPI, CORS, rate limiter.
- Frontend: projeto Next.js, layout base, design system (Tailwind), Lighthouse CI.

## Fase 1 — Identidade e e-mails
- RF-001..009, RF-060: cadastro, login, refresh, logout, senha, **recuperação de senha e confirmação de e-mail**.
- `IEmailSender` + templates base + provedor escolhido.
- Frontend: login, cadastro, recuperar senha, confirmar e-mail.

## Fase 2 — Catálogo com variações e SEO
- Cores, tamanhos, produtos, **grade de variações**, imagens, categorias (RF-020..023, RF-050/051/056/057).
- SEO: páginas renderizadas no servidor, metadata, JSON-LD, sitemap, redirects 301, revalidação sob demanda.
- Cache HybridCache.
- Frontend: home, categoria com filtros, página de produto com seletor de cor/tamanho; admin de catálogo.

## Fase 3 — Estoque, carrinho e frete
- Estoque por variação (RF-052), carrinho + merge (RF-030/031).
- **Integração Correios** (ou agregador), regras de frete, simulador por CEP (RF-032, RF-058).
- Frontend: carrinho com frete; admin de estoque e regras de frete.

## Fase 4 — Checkout e pagamento
- Endereços (RF-007), checkout transacional (RF-040), **pagamento integrado PIX/cartão + webhook** (RF-044/045), cancelamento com estorno (RF-043).
- **Cancelamento automático em 48 h** e lembrete de 24 h (RF-046).
- E-mails de pedido (EM-04..EM-10).
- Testes de concorrência de estoque, webhooks duplicados/fora de ordem.
- Frontend: checkout completo, tela de pagamento PIX, meus pedidos.

## Fase 5 — Backoffice de pedidos e usuários
- RF-053 (status, rastreio), RF-054 (usuários), RF-055 (dashboard), estornos pendentes.
- Frontend: telas admin correspondentes.

## Fase 6 — Endurecimento e lançamento
- Revisão de segurança, testes de carga (k6), E2E dos fluxos críticos, LGPD.
- **Homologação com pagamentos reais de baixo valor** em produção e estorno.
- Search Console, Merchant Center, SPF/DKIM/DMARC, backup, alertas.

## Pós-lançamento (backlog)
- Etiquetas de envio pela API e atualização automática de entrega pelo rastreio.
- Cupons e promoções, avaliações, 2FA para admin, e-mail marketing (com opt-in).
