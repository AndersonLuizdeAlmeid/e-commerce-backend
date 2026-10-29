# 19 — Roadmap

**Status:** Em revisão

Entregas incrementais em **fatias verticais**: cada fase vai do banco à tela e termina com testes verdes, deploy em staging e demonstração ao cliente.

**Banco incremental:** cada fase cria só as migrations das tabelas que usa. O DDL do arquivo 05 é a referência, não um script único a aplicar no início.

**Infraestrutura sob demanda:** peças transversais entram na primeira fase que precisa delas (rate limiter e Mailpit na Fase 2, Outbox e jobs na Fase 4, por exemplo), e não todas na Fase 0.

## Fase 0 — Esqueleto ponta a ponta ✅ (19/10/2026)
- Solution em Clean Architecture, `Directory.*.props`, `global.json`, `.editorconfig`, Dockerfile multi-stage, Docker Compose (Postgres + migrator + API), CI no GitHub Actions.
- Persistência: `NpgsqlDataSource`, Dapper, DbUp (`ECommerce.Migrator`) com seed de exemplo só em Development.
- Pipeline HTTP: ProblemDetails + `Result`, correlation id, Serilog, health checks (`live`/`ready`), OpenAPI + Scalar, CORS.
- Primeira fatia: `GET /catalog/categories` (RF-022) e `GET /catalog/categories/{slug}`.
- Testes: arquitetura (NetArchTest) e funcionais com Testcontainers + Respawn.
- Frontend: Next.js 16 + Tailwind, home e página de categoria renderizadas no servidor (ISR, metadata, canonical, 404 real), tipos gerados do OpenAPI, Vitest, CI.

## Fase 1 — Vitrine: catálogo com variações e SEO 🔎 (implementada em 20/10/2026, em validação)
> Objetivos, escopo e checklist de aceite: [entregas/fase-1-vitrine.md](entregas/fase-1-vitrine.md).
- Cores, tamanhos, produtos, **grade de variações** e imagens (leitura, RF-020, RF-021, RF-023), com dados de seed.
- SEO: metadata, JSON-LD, sitemap, robots, redirects 301, 410 para inativos.
- Cache HybridCache + `Cache-Control`/`ETag` nos endpoints públicos.
- *Antecipado da Fase 3:* só a tabela `inventory.stock_items` (saldo), porque a vitrine precisa mostrar "disponível sim/não" (RF-021 CA2). Movimentações e ajustes continuam na Fase 3.
- *Adiado:* redirect 301 de **categoria** (a tabela `slug_history` já aceita `Category`, mas slug de categoria só muda pelo admin, na Fase 2) e o feed do Merchant Center (Fase 6).
- Frontend: home, categoria com filtros, página de produto com seletor de cor/tamanho; Lighthouse CI.
- *Ordem trocada com a identidade (decisão de 19/10/2026): a vitrine é o que o cliente vê primeiro e o que mais pesa no SEO.*

## Fase 2 — Identidade, e-mails e admin de catálogo (dividida em 2A e 2B em 22/10/2026)
> **2A — Contas e e-mails** 🔎 implementada em 26/10/2026, em validação: [entregas/fase-2a-contas-e-emails.md](entregas/fase-2a-contas-e-emails.md). Antecipa o **Outbox** (era da Fase 4) para os e-mails de conta.
> **2B — Painel de catálogo**: próxima.

- RF-001..009, RF-060: cadastro, login, refresh, logout, senha, **recuperação de senha e confirmação de e-mail**.
- `IEmailSender` + templates base + provedor escolhido (Mailpit em dev). Rate limiter (arquivo 11).
- Admin de catálogo (RF-050/051/056/057, RF-059), que depende da autenticação.
- Herdado da Fase 1: invalidação do HybridCache por tag após cada escrita do admin, revalidação sob demanda das páginas (`/api/revalidate`), `slug_history` preenchida ao trocar slug (produto **e** categoria) e domínio do CDN de imagens no `next.config` (depende da D06).
- Frontend: login, cadastro, recuperar senha, confirmar e-mail; admin de catálogo.

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
