# 18 — DevOps e Ambientes

**Status:** Em revisão. Ambiente de dev na VM com deploy automático desde 07/11/2026 (§5).

## 1. Ambientes

| Ambiente | Propósito | Dados |
|----------|-----------|-------|
| `Development` | Máquina do dev (Docker Compose) | Seed de exemplo |
| Dev na VM (`Development`) | Ambiente compartilhado para testar cada versão da `main`, só pela rede privada (Tailscale) | Seed de exemplo |
| `Testing` | CI (Testcontainers) | Efêmeros |
| `Staging` | Homologação | Anonimizados/sintéticos |
| `Production` | Produção | Reais |

## 2. Docker

- **Dockerfile multi-stage** da API (`sdk` → `aspnet` runtime, imagem *chiseled*/non-root), porta 8080.
- Imagem da loja (repositório do frontend): Next.js `standalone` em `node:22-alpine`, usuário `node`, sem npm no runtime.
- Dev na VM: `deploy/dev/compose.yml`, com as imagens do GHCR fixadas por digest, portas só em `127.0.0.1` e containers
  sem root, sem capabilities e, onde dá, com sistema de arquivos só de leitura.
- `docker-compose.yml` (máquina do dev):
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
4. `dotnet list package --vulnerable` (CodeQL e gitleaks ainda não entraram)
5. Imagens da API e do migrator: build, **Trivy** (barra vulnerabilidade crítica com correção; altas só no log) e, só
   na `main`, push no GHCR com SBOM e proveniência, tag `sha-<commit>`
6. Só na `main`: deploy no dev da VM (§5)

Frontend (repo próprio): `npm ci` → lint → typecheck → vitest → build contra API falsa → Lighthouse CI; depois a imagem
da loja (Trivy, GHCR) e, na `main`, o deploy. Playwright contra API em container ainda não entrou.

Todas as ações do GitHub fixadas por SHA; o Trivy roda pela imagem oficial fixada por digest.

## 5. CD

**Hoje (desde 07/11/2026): push na `main` → dev na VM.** Entrega, decisões e validação em
[entregas/infra-1-ambiente-dev.md](entregas/infra-1-ambiente-dev.md); operação em `deploy/dev/README.md`.

- O job `deploy` (ambiente `dev` do GitHub, restrito à `main`) entra na rede privada pelo Tailscale com um nó
  temporário (`tag:ci`, que só alcança a porta 22 da VM) e chama por SSH o `mo-deploy`, único comando que a chave
  executa. Ele só aceita digests de imagem e commits da `main`, roda o migrator antes da API, espera a saúde por
  2 minutos e, se falhar, volta sozinho para a versão anterior (migrations não são desfeitas).
- Backend e loja são publicados cada um pelo seu repositório; mudança que mexe nos dois vai backend primeiro.
- Os segredos do ambiente são gerados na própria VM e nunca passam pelo GitHub.

**Depois, com domínio e VM própria (fora da Infra 1):**

- `main` → deploy automático em **Staging** (migrator roda antes da API).
- Tag `vX.Y.Z` → deploy em **Production** com aprovação manual.
- Hospedagem: **VM única** (arquivo 20, T07) — ver §7. Deploy pelo mesmo mecanismo do dev (comando forçado, digests, volta automática).
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
- **Backup:** `pg_dump` diário compactado para o Cloudflare R2 (mesmo bucket das fotos, prefixo privado), retenção de 7 dias, e teste de restauração mensal. No dev da VM, por enquanto, o backup fica na própria VM (`mo-backup`/`mo-restore`, 7 dias, só o banco).
- **Riscos aceitos no MVP:** ponto único de falha e manutenção do SO por nossa conta (atualizações automáticas de segurança ligadas). Quando o volume crescer: banco gerenciado primeiro, depois segunda instância da API (aí entra Redis como L2 do HybridCache).
- **Staging:** na mesma VM, outro `docker compose` com portas e banco próprios, enquanto couber na memória.
