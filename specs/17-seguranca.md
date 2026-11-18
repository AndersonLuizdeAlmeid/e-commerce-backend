# 17 — Segurança

**Status:** Em revisão

Referências: **OWASP Top 10 (2021/2025)**, **OWASP API Security Top 10 (2023)**, **OWASP ASVS nível 2**.

## 1. Controles por risco

| Risco | Controle |
|-------|----------|
| Broken Object Level Authorization (IDOR) | `customer_id` sempre do token; filtro no SQL; `404` para recurso alheio; testes (arquivo 09/15) |
| Broken Authentication | JWT curto, refresh rotativo com detecção de reuso, lockout, rate limit, hash PBKDF2/Argon2 (arquivo 08) |
| Broken Function Level Authorization | Fallback policy autenticada; policy Admin no **grupo** `/admin`; teste que varre endpoints |
| Mass Assignment | DTOs de request específicos por caso de uso; nunca bind direto em entidade; `role`, `price` de pedido, `total` jamais aceitos do cliente |
| Injection (SQL) | Dapper **sempre parametrizado**; ordenação por whitelist; revisão de SQL em PR |
| Unrestricted Resource Consumption | Rate limiting, `pageSize` máx. 100, limite de tamanho de body (`MaxRequestBodySize` 1 MB), timeouts de comando SQL (30 s) |
| Security Misconfiguration | Headers de segurança, docs só fora de produção, sem stack trace, CORS restrito |
| Sensitive Data Exposure | HTTPS + HSTS; não logar dados sensíveis; refresh token salvo como hash |
| Vulnerable Components | Dependabot/Renovate; `dotnet list package --vulnerable` e `npm audit` no CI |
| SSRF | URLs de imagem validadas (esquema `https`, domínio permitido); upload valida tipo real (magic bytes), tamanho máx. e reprocessa a imagem |
| Fraude em pagamentos | Valor sempre calculado no servidor; status só por webhook assinado + consulta à plataforma; idempotência por evento; antifraude da própria plataforma |
| Dados de cartão (PCI-DSS) | Cartão capturado apenas pelo componente da plataforma (tokenização); a loja nunca recebe, loga ou armazena número/CVV — escopo SAQ-A |
| Enumeração via "esqueci a senha" | Resposta sempre igual (202) + rate limit |

## 2. Headers HTTP (API)

- `Strict-Transport-Security: max-age=31536000; includeSubDomains` (produção)
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY` / `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (API só retorna JSON)
- `Referrer-Policy: no-referrer`
- Remover `Server` (`Kestrel: AddServerHeader = false`).

- `Cache-Control: no-store` em toda resposta que não declara cache: só o catálogo público é cacheável (entrega 6.2, ASVS V8.2.1).

Frontend (Next.js, entrega 6.2): CSP **sem nonce**, para não perder o ISR (`script-src 'self' 'unsafe-inline'` + Mercado Pago; `connect-src` = API e Mercado Pago; `frame-src` = Mercado Pago; `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`, `frame-ancestors 'none'`). Começa em `Report-Only` com relatório em `/api/csp-report`; `CSP_MODE=enforce` bloqueia. Também `nosniff`, `DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, HSTS com https e `Permissions-Policy` com câmera só no painel. O zod roda sem JIT (`@/shared/lib/zod`), porque a CSP não libera `eval`.

## 3. Segredos

- **Nunca** em `appsettings*.json` versionado: chave JWT, connection string de produção, senha do admin seed, **credenciais da plataforma de pagamento e segredo do webhook, token dos Correios/Melhor Envio, chave do provedor de e-mail, segredo de revalidação do Next.js**.
- Dev: `dotnet user-secrets` / `.env` (no `.gitignore`).
- Produção (VM única, arquivo 18 §7): arquivo `.env` só na VM (permissão 600, fora do git) lido pelo Docker Compose; GitHub Actions guarda os segredos de deploy. A API recusa subir com a chave JWT de desenvolvimento (marcador `DEV-ONLY`) fora de Development.
- Scan de segredos no CI: **gitleaks** no histórico inteiro, em imagem fixada por digest (entrega 6.2); falsos positivos conferidos ficam no `.gitleaksignore`. O GitHub secret scanning vale por serem repositórios públicos.

## 4. Banco de dados

- Usuário da aplicação com **privilégios mínimos** (DML nos schemas da aplicação); usuário separado para migrations (DDL). *Implementado na 6.2*: o migrator cria o papel da API (`Database:AppRole`/`AppPassword`, ex. `mo_app`) e reaplica as permissões a cada deploy; a API só lê o journal das migrations e não cria nada, nem em `public`. Os testes funcionais rodam a API com esse papel.
- Conexão com SSL em produção (`SSL Mode=Require`) **quando o banco sair da VM**. Com o Postgres na rede interna do Compose, sem porta publicada, fica sem SSL (entrega 6 §3, P6).

## 5. LGPD

- Dados pessoais coletados: nome, e-mail, telefone, CPF (opcional), endereços. Coletar apenas o necessário.
- Base legal: execução de contrato (pedidos). Política de privacidade e termos no frontend.
- Direitos do titular (evolução próxima ao MVP): **exportar dados** (`GET /me/data-export`) e **excluir conta** = anonimização (pedidos são mantidos por obrigação fiscal, com dados pessoais substituídos).
- CPF mascarado em logs e em telas de admin (exibir `***.456.789-**`).

## 6. Processo

- Revisão de segurança em PRs que tocam auth/autorização/SQL dinâmico.
- SAST: analisadores .NET (`Microsoft.CodeAnalysis.NetAnalyzers`, `SecurityCodeScan`), ESLint security no frontend; **CodeQL** (`security-extended`) nos dois repositórios, em PR, na `main` e toda segunda (entrega 6.2).
- Dependências: **Dependabot** semanal (NuGet ou npm, ações do GitHub e Docker); `npm audit --audit-level=high` e `dotnet list package --vulnerable` bloqueiam o CI.
