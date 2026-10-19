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

Frontend (Next.js): CSP restritiva (`script-src 'self'` + domínios da plataforma de pagamento, `connect-src` = API e plataforma, `frame-src` = plataforma), demais headers acima.

## 3. Segredos

- **Nunca** em `appsettings*.json` versionado: chave JWT, connection string de produção, senha do admin seed, **credenciais da plataforma de pagamento e segredo do webhook, token dos Correios/Melhor Envio, chave do provedor de e-mail, segredo de revalidação do Next.js**.
- Dev: `dotnet user-secrets` / `.env` (no `.gitignore`).
- Produção: variáveis de ambiente injetadas pelo provedor / secret manager (Azure Key Vault, AWS Secrets Manager, Doppler — ver arquivo 20).
- Scan de segredos no CI (gitleaks / GitHub secret scanning).

## 4. Banco de dados

- Usuário da aplicação com **privilégios mínimos** (DML nos schemas da aplicação); usuário separado para migrations (DDL).
- Conexão com SSL em produção (`SSL Mode=Require`).

## 5. LGPD

- Dados pessoais coletados: nome, e-mail, telefone, CPF (opcional), endereços. Coletar apenas o necessário.
- Base legal: execução de contrato (pedidos). Política de privacidade e termos no frontend.
- Direitos do titular (evolução próxima ao MVP): **exportar dados** (`GET /me/data-export`) e **excluir conta** = anonimização (pedidos são mantidos por obrigação fiscal, com dados pessoais substituídos).
- CPF mascarado em logs e em telas de admin (exibir `***.456.789-**`).

## 6. Processo

- Revisão de segurança em PRs que tocam auth/autorização/SQL dinâmico.
- SAST: analisadores .NET (`Microsoft.CodeAnalysis.NetAnalyzers`, `SecurityCodeScan`), ESLint security no frontend; CodeQL no GitHub.
