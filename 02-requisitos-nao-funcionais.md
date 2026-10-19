# 02 — Requisitos Não Funcionais

**Status:** Em revisão

| ID | Categoria | Requisito |
|----|-----------|-----------|
| RNF-001 | Performance | p95 ≤ **200 ms** para leituras de catálogo (com cache quente) e ≤ **500 ms** para checkout, com 100 req/s em ambiente de referência. |
| RNF-002 | Performance | Toda listagem **DEVE** ser paginada; `pageSize` máximo = 100. Proibido `SELECT *` e N+1. |
| RNF-003 | Escalabilidade | A API **DEVE** ser *stateless* (nenhum estado de sessão em memória que impeça escalar horizontalmente). Cache distribuído opcional (Redis). |
| RNF-004 | Disponibilidade | Health checks de *liveness* e *readiness* (arquivo 14). Meta inicial: 99,5%. |
| RNF-005 | Consistência | Operações de pedido/estoque **DEVEM** ser transacionais (ACID) e protegidas contra condições de corrida (arquivo 06). |
| RNF-006 | Segurança | OWASP ASVS nível 2 como referência (arquivo 17). HTTPS obrigatório fora de dev. |
| RNF-007 | Segurança | Autenticação JWT + refresh token rotativo; autorização por perfil e por recurso (arquivos 08 e 09). |
| RNF-008 | Proteção | Rate limiting por IP e por usuário (arquivo 11). |
| RNF-009 | Observabilidade | Logs estruturados (JSON), correlation id em todas as requisições, tracing OpenTelemetry (arquivo 14). |
| RNF-010 | Manutenibilidade | Clean Architecture + DDD (arquivos 03 e 04). Regras de arquitetura verificadas por testes (NetArchTest). |
| RNF-011 | Qualidade | Cobertura mínima: **90%** no Domain, **80%** na Application. Build falha com warnings (`TreatWarningsAsErrors`). |
| RNF-012 | Portabilidade | Tudo roda via `docker compose up` (API + Postgres [+ Redis]). |
| RNF-013 | Compatibilidade | API versionada (`/api/v1`). Mudanças incompatíveis só em nova versão. |
| RNF-014 | Documentação | OpenAPI gerado automaticamente; UI de documentação apenas em ambientes não produtivos. |
| RNF-015 | Privacidade | Conformidade com LGPD: dados pessoais mínimos, possibilidade de exportar/anonimizar (arquivo 17). |
| RNF-016 | Internacionalização | Mensagens de erro ao usuário em **pt-BR**; datas armazenadas em **UTC** (`timestamptz`), exibidas no fuso do cliente. |
| RNF-017 | Dinheiro | Valores monetários como `decimal` (C#) / `numeric(12,2)` (Postgres). **Nunca** `float/double`. |
| RNF-018 | Acessibilidade | Frontend em conformidade WCAG 2.1 AA (arquivo 16). |
| RNF-019 | SEO / Web Vitals | Core Web Vitals no verde (LCP < 2,5 s, INP < 200 ms, CLS < 0,1) e Lighthouse SEO ≥ 95 nas páginas públicas (arquivo 24). |
| RNF-020 | Integrações | Toda chamada externa (pagamento, Correios, e-mail) com timeout, retry e circuit breaker; falha de integração nunca deixa pedido/estoque inconsistente. |
| RNF-021 | Confiabilidade | Efeitos externos via Outbox: nenhum e-mail ou estorno perdido nem disparado para transação desfeita (arquivo 25). |
| RNF-022 | Pagamentos | Loja fora do escopo de dados de cartão (PCI-DSS SAQ-A): cartão capturado só pelo componente da plataforma (arquivo 21). |
