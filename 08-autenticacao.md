# 08 — Autenticação

**Status:** Em revisão

## 1. Estratégia

**JWT (access token) de curta duração + refresh token opaco rotativo**, implementados na própria API (sem ASP.NET Identity com EF, sem provedor externo no MVP).

| Token | Formato | Duração | Onde fica no cliente | Onde fica no servidor |
|-------|---------|---------|----------------------|-----------------------|
| Access token | JWT assinado | **15 min** | **memória** do SPA (nunca `localStorage`) | não armazenado (stateless) |
| Refresh token | 64 bytes aleatórios (base64url) | **7 dias** (deslizante, máx. 30 dias por família) | cookie `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` | **hash SHA-256** em `identity.refresh_tokens` |

## 2. JWT

- Algoritmo: **HS256** no MVP com chave ≥ 256 bits vinda de segredo (env/secret manager). Evolução: RS256/ES256 com rotação de chaves (`kid`).
- Claims:
  | Claim | Valor |
  |-------|-------|
  | `sub` | userId |
  | `email` | e-mail |
  | `role` | `Customer` \| `Admin` |
  | `sstamp` | security stamp (invalidação após troca de senha/role) |
  | `jti` | id único |
  | `iss` / `aud` | configuráveis (`ecommerce-api` / `ecommerce-web`) |
- Validação: `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`, `ValidateIssuerSigningKey` = `true`; `ClockSkew = 30s`; `MapInboundClaims = false`.
- **Checagem de security stamp**: em `OnTokenValidated`, para rotas sensíveis (admin e mudança de dados), comparar `sstamp` com o valor atual (cacheado por 1 min) — permite revogar sessões de admin rebaixado sem esperar expiração.

## 3. Senhas

- Hash com `PasswordHasher<User>` de `Microsoft.Extensions.Identity.Core` (PBKDF2-HMAC-SHA512, 100k+ iterações, salt por usuário), usado de forma isolada — **sem** EF. Alternativa: Argon2id (ver arquivo 20).
- `PasswordVerificationResult.SuccessRehashNeeded` → re-hash transparente no login.
- Política: mínimo 8 caracteres, maiúscula, minúscula, dígito; máx. 128. DEVERIA recusar senhas de lista de vazamentos (top 10k) — opcional.

## 4. Fluxos

### 4.1 Registro
1. Valida payload → normaliza e-mail → verifica unicidade.
2. Cria `User(Customer)` com hash e `SecurityStamp`.
3. Evento `UserRegistered` → cria `Customer` e `Cart`.
4. Grava no Outbox o e-mail de boas-vindas/confirmação (EM-01).
5. Retorna `201` já com a sessão iniciada (access token + cookie de refresh).

### 4.2 Login
1. Busca usuário por e-mail. **Se não existir, executa hash fictício** para tempo de resposta constante (evita enumeração por timing).
2. Se bloqueado (`lockout_end_utc > now`) → `401` genérico.
3. Verifica senha; em falha incrementa `failed_login_attempts` (≥5 → lockout 15 min).
4. Em sucesso: zera contador, gera access token + refresh token (nova `family_id`), grava hash do refresh, seta cookie.

### 4.3 Refresh (com rotação e detecção de reuso)
1. Lê cookie → calcula SHA-256 → busca token.
2. Não encontrado/expirado → `401`.
3. **Já revogado** → indica roubo/reuso → revoga **todos os tokens da família** → `401`.
4. Válido → revoga o atual (`revoked_at`, `replaced_by_id`), emite novo par na mesma família.
5. Verifica se usuário continua ativo.

### 4.4 Logout
- Revoga refresh token atual e expira o cookie. Access token expira naturalmente (≤15 min).

### 4.5 Troca de senha / desativação / mudança de role
- Atualiza `security_stamp` e revoga todos os refresh tokens do usuário.

### 4.6 Recuperação de senha e confirmação de e-mail
- Tokens aleatórios de 32 bytes, armazenados como hash em `identity.user_tokens`, uso único (reset: 1 h; confirmação: 24 h). Detalhes no arquivo 23.
- Reset de senha revoga todas as sessões.

## 5. CSRF

- O refresh usa cookie, então o endpoint `/auth/refresh` é protegido por: `SameSite=Strict` + exigência de header customizado (`X-Requested-With: ecommerce-web`) + validação de `Origin` contra a whitelist do CORS.
- Demais endpoints usam `Authorization: Bearer` (não sujeitos a CSRF).

## 6. Configuração

```json
"Jwt": {
  "Issuer": "ecommerce-api",
  "Audience": "ecommerce-web",
  "AccessTokenMinutes": 15,
  "RefreshTokenDays": 7,
  "RefreshTokenAbsoluteDays": 30
  // "SigningKey" via variável de ambiente / secret — NUNCA no appsettings versionado
}
```

## 7. Limpeza

- Job em background (`BackgroundService`) diário remove refresh tokens expirados/revogados há mais de 30 dias.

## 8. Evolução
- 2FA (TOTP) para administradores.
- Login social (Google) via OIDC.
