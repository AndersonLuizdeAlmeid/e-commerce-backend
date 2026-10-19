# 18 — DevOps e Ambientes

**Status:** Em revisão

## 1. Ambientes

| Ambiente | Propósito | Dados |
|----------|-----------|-------|
| `Development` | Máquina do dev (Docker Compose) | Seed de exemplo |
| `Testing` | CI (Testcontainers) | Efêmeros |
| `Staging` | Homologação | Anonimizados/sintéticos |
| `Production` | Produção | Reais |

## 2. Docker

- **Dockerfile multi-stage** da API (`sdk` → `aspnet` runtime, imagem *chiseled*/non-root), porta 8080.
- `docker-compose.yml` (dev):
  ```yaml
  services:
    postgres:
      image: postgres:17
      environment: { POSTGRES_DB: ecommerce, POSTGRES_USER: ecommerce, POSTGRES_PASSWORD: ecommerce }
      ports: ["5432:5432"]
      volumes: [pgdata:/var/lib/postgresql/data]
      healthcheck: { test: ["CMD-SHELL", "pg_isready -U ecommerce"], interval: 5s }
    migrator:
      build: { context: ., target: migrator }
      depends_on: { postgres: { condition: service_healthy } }
    api:
      build: .
      depends_on: { migrator: { condition: service_completed_successfully } }
      ports: ["5001:8080"]
      env_file: .env
    mailpit:                       # captura e-mails em dev (UI em http://localhost:8025)
      image: axllent/mailpit
      ports: ["8025:8025", "1025:1025"]
    # redis: (opcional, quando UseRedis=true)
    # aspire-dashboard: (opcional, OTLP local)
  volumes: { pgdata: {} }
  ```

## 3. Configuração

- Hierarquia: `appsettings.json` → `appsettings.{Env}.json` → variáveis de ambiente → secrets.
- **Options pattern** com validação (`ValidateDataAnnotations().ValidateOnStart()`) para `Jwt`, `Cors`, `RateLimiting`, `Caching`, `ConnectionStrings`.
- `.env.example` versionado documenta todas as variáveis; `.env` no `.gitignore`.

## 4. CI (GitHub Actions)

Pipeline em PRs e `main`:
1. `dotnet restore` (com cache) → `dotnet format --verify-no-changes`
2. `dotnet build -c Release` (warnings = erro)
3. `dotnet test` (unit + integração + funcionais + arquitetura) com cobertura → gate de cobertura
4. `dotnet list package --vulnerable` + CodeQL + gitleaks
5. Build da imagem Docker (push somente em `main`/tags)

Frontend (repo próprio): `npm ci` → lint → typecheck → vitest → build → Playwright (contra API em container).

## 5. CD

- `main` → deploy automático em **Staging** (migrator roda antes da API).
- Tag `vX.Y.Z` → deploy em **Production** com aprovação manual.
- Hospedagem: a definir (arquivo 20, T07) — sugestão: frontend Next.js na **Vercel** (ou container Node); API + PostgreSQL gerenciado (Azure Container Apps + Azure Database for PostgreSQL, AWS ECS + RDS, ou Railway).
- Ambientes de **sandbox** da plataforma de pagamento e dos Correios/Melhor Envio em Development/Staging; credenciais de produção apenas em Production.
- Webhooks em dev: túnel (ex.: `ngrok`/Cloudflare Tunnel) apontando para a API local.

## 6. Padrões do repositório

- `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`.
- `Directory.Packages.props`: versões centralizadas.
- `.editorconfig` com regras de estilo C#.
- **Conventional Commits** (`feat:`, `fix:`, `test:`, `docs(spec):` ...) e branch por feature; PR com revisão obrigatória e CI verde.
- `global.json` fixando SDK .NET 10.
