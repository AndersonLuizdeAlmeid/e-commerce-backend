# 11 — Rate Limiting

**Status:** Em revisão

## 1. Objetivo

Proteger a API contra abuso (força bruta de login, scraping do catálogo, flood de pedidos) usando o middleware nativo **`Microsoft.AspNetCore.RateLimiting`** (`System.Threading.RateLimiting`).

## 2. Particionamento

- **Requisições anônimas**: por **IP do cliente** (`RemoteIpAddress`, após `ForwardedHeaders` configurado com proxies confiáveis — sem isso todos os usuários atrás do proxy dividiriam o mesmo limite, ou o IP seria falsificável via header).
- **Requisições autenticadas**: por **`sub` (userId)**.

## 3. Policies

| Policy | Algoritmo | Limite | Partição | Aplicado em |
|--------|-----------|--------|----------|-------------|
| **Global** (limiter global) | Token bucket | 100 tokens, reposição 50/10s | IP/usuário | Toda a API |
| `auth-strict` | Fixed window | **5 req / 1 min** | IP | `POST /auth/login`, `/auth/register`, `/auth/forgot-password`, `/auth/reset-password` |
| `auth-refresh` | Fixed window | 20 req / 1 min | IP | `POST /auth/refresh` |
| `public-read` | Sliding window | 120 req / 1 min (6 segmentos) | IP | `GET /catalog/**` |
| `customer-write` | Token bucket | 30 tokens, reposição 10/10s | userId | `POST/PUT/DELETE` de cart, me |
| `checkout` | Fixed window | **5 req / 1 min** | userId | `POST /orders` |
| `admin` | Token bucket | 300 tokens, reposição 100/10s | userId | `/admin/**` |
| `shipping-quote` | Sliding window | 20 req / 1 min | IP/usuário | `POST /shipping/quotes` (cada chamada pode custar uma requisição aos Correios) |
| `payment` | Fixed window | 5 req / 1 min | userId | `POST /orders/{id}/payments` |
| `webhooks` | Token bucket | 600 tokens, reposição 100/s | IP | `/webhooks/**` (alto, para não perder notificações legítimas) |

- Valores **configuráveis** via `appsettings` (`RateLimiting:Policies:<nome>`), ajustáveis sem recompilar.
- `QueueLimit = 0` (rejeita imediatamente; não segura conexões).
- Health checks **excluídos** (`DisableRateLimiting()`).
- O servidor do Next.js chama a API em nome de muitos visitantes: suas requisições internas (rede privada ou header com segredo) usam uma partição própria com limite maior, para não estourar o `public-read`.

## 4. Resposta de rejeição

- Status **`429 Too Many Requests`** + header `Retry-After` (segundos) + corpo ProblemDetails:
  ```json
  { "type": "https://httpstatuses.io/429", "title": "Muitas requisições", "status": 429,
    "detail": "Limite de requisições excedido. Tente novamente em 42 segundos.", "traceId": "..." }
  ```
- Log em nível `Warning` com partição (IP mascarado/userId) e policy; métrica `ratelimit.rejected` por policy.

## 5. Ordem no pipeline

`UseForwardedHeaders → UseRouting → UseCors → UseAuthentication → UseRateLimiter → UseAuthorization`

> `UseRateLimiter` **depois** de `UseAuthentication` para que as policies por usuário enxerguem o `sub`.

## 6. Limitações e evolução

- O rate limiter nativo é **em memória por instância**. Com N instâncias, o limite efetivo é ~N×. Aceitável no MVP (1–2 instâncias).
- Evolução: limitar no **gateway/reverse proxy** (Nginx, Traefik, Cloudflare/WAF) ou rate limiter distribuído com Redis.
- Proteção específica de login também existe no domínio (lockout por conta — arquivo 08), complementar ao limite por IP.

## 7. Critérios de aceite

- **CA1** 6ª tentativa de login em 1 min do mesmo IP → `429` com `Retry-After`.
- **CA2** Limites de um usuário não afetam outro usuário.
- **CA3** `/health/*` nunca retorna `429`.
- **CA4** Testes funcionais com configuração de limites reduzidos validam CA1–CA3.
