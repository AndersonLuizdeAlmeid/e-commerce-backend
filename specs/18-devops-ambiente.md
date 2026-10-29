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
- Hospedagem: **VM única** (arquivo 20, T07) — ver §7. Deploy = `docker compose pull && docker compose up -d` via SSH a partir do GitHub Actions.
- Ambientes de **sandbox** da plataforma de pagamento e dos Correios/Melhor Envio em Development/Staging; credenciais de produção apenas em Production.
- Webhooks em dev: túnel (ex.: `ngrok`/Cloudflare Tunnel) apontando para a API local.

## 6. Padrões do repositório

- `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`.
- `Directory.Packages.props`: versões centralizadas.
- `.editorconfig` com regras de estilo C#.
- **Conventional Commits** (`feat:`, `fix:`, `test:`, `docs(spec):` ...) e branch por feature; PR com revisão obrigatória e CI verde.
- `global.json` fixando SDK .NET 10.

## 7. Hospedagem (T07, decidido em 21/10/2026)

Volume inicial baixo (D09: ~10 pedidos/semana), então **tudo numa VM** com Docker Compose:

```
Cloudflare (DNS, TLS, CDN, proteção — plano grátis)
        │
     VM Linux ──┬── Caddy (HTTPS, proxy reverso)
                ├── web   Next.js (output standalone)      loja.com.br
                ├── api   ECommerce.Api                     api.loja.com.br
                ├── migrator (roda e sai, antes da api)
                └── postgres 17 (volume local)
```

- **Tamanho mínimo:** 2 vCPU e **4 GB de RAM** (Next.js + .NET + PostgreSQL juntos). Preferir região **São Paulo** (latência e TTFB para o Google); fora do Brasil, a CDN da Cloudflare compensa nas páginas em cache.
- **Por que não Vercel:** o plano Hobby da Vercel não permite uso comercial; o Pro é pago por mês e separaria front e API em dois lugares.
- **Backup:** `pg_dump` diário compactado para o Cloudflare R2 (mesmo bucket das fotos, prefixo privado), retenção de 7 dias, e teste de restauração mensal.
- **Riscos aceitos no MVP:** ponto único de falha e manutenção do SO por nossa conta (atualizações automáticas de segurança ligadas). Quando o volume crescer: banco gerenciado primeiro, depois segunda instância da API (aí entra Redis como L2 do HybridCache).
- **Staging:** na mesma VM, outro `docker compose` com portas e banco próprios, enquanto couber na memória.
