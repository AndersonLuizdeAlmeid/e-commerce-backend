# 10 — CORS

**Status:** Em revisão

## 1. Contexto

O frontend React é servido em origem diferente da API (ex.: `https://loja.exemplo.com` → `https://api.exemplo.com`; em dev `http://localhost:3000` (Next.js) → `http://localhost:5001`). O navegador exige CORS. Como o refresh token usa **cookie**, as requisições do SPA usam `credentials: 'include'`, o que proíbe `AllowAnyOrigin`.

## 2. Política

Uma única policy nomeada `"Frontend"`, aplicada globalmente:

| Item | Valor |
|------|-------|
| Origens | **Whitelist explícita** via configuração (`Cors:AllowedOrigins`). Sem wildcard. |
| Métodos | `GET, POST, PUT, PATCH, DELETE` (OPTIONS implícito) |
| Headers permitidos | `Authorization, Content-Type, Idempotency-Key, X-Correlation-Id, X-Requested-With` |
| Headers expostos | `Location, X-Correlation-Id, Retry-After, X-Pagination-Total` |
| Credenciais | `AllowCredentials()` |
| Preflight cache | `SetPreflightMaxAge(10 min)` |

```csharp
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("Frontend", p => p
    .WithOrigins(origins)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-Id", "X-Requested-With")
    .WithExposedHeaders("Location", "X-Correlation-Id", "Retry-After")
    .AllowCredentials()
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
// ...
app.UseCors("Frontend"); // depois de UseRouting, antes de UseRateLimiter/UseAuthentication
```

## 3. Configuração por ambiente

| Ambiente | `Cors:AllowedOrigins` |
|----------|-----------------------|
| Development | `http://localhost:3000` |
| Staging | `https://staging.loja.exemplo.com` |
| Production | `https://loja.exemplo.com`, `https://www.loja.exemplo.com` |

- Startup **DEVE** falhar se a lista estiver vazia fora de Development (validação de options com `ValidateOnStart`).

## 4. Critérios de aceite

- **CA1** Preflight de origem permitida → `204` com `Access-Control-Allow-Origin` = origem exata e `Access-Control-Allow-Credentials: true`.
- **CA2** Origem não permitida → resposta sem headers CORS (o navegador bloqueia).
- **CA3** Testes funcionais cobrem CA1 e CA2.

## 5. Next.js e webhooks

- Chamadas feitas **no servidor** pelo Next.js (páginas públicas) não passam por CORS (servidor → servidor).
- CORS continua necessário para as chamadas do **navegador** (carrinho, checkout, conta, admin).
- `/api/v1/webhooks/**` **não** recebe CORS (é chamado por servidores da plataforma de pagamento, não por navegadores).

## 6. Observação

CORS **não é** mecanismo de segurança do servidor (só protege o navegador). Autorização e proteção CSRF continuam obrigatórias (arquivos 08 e 09). Alternativa a considerar: servir o SPA e a API sob o **mesmo domínio** via reverse proxy (`/api` → API), o que elimina CORS — ver arquivo 20.
