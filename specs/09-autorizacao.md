# 09 — Autorização

**Status:** Em revisão

## 1. Modelo

**RBAC simples (2 perfis)** + **autorização por recurso (ownership)**, implementado com **policies** do ASP.NET Core.

| Perfil | Descrição |
|--------|-----------|
| `Customer` | Atribuído no cadastro. Acessa apenas seus próprios dados. |
| `Admin` | Acesso ao backoffice. Criado via seed (RF-060) ou promovido por outro admin. |

> Um usuário tem **um único** perfil no MVP. Se no futuro houver perfis granulares (ex.: "Estoquista", "Atendimento"), migrar para **permissões** (`products:write`, `orders:manage`) mantendo as policies como fachada — os endpoints não mudam.

## 2. Policies

```csharp
services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()) // seguro por padrão
    .AddPolicy(Policies.Customer, p => p.RequireRole(Roles.Customer))
    .AddPolicy(Policies.Admin,    p => p.RequireRole(Roles.Admin).AddRequirements(new ActiveSecurityStampRequirement()));
```

- **Fallback policy exige autenticação**: todo endpoint é protegido por padrão; endpoints públicos **DEVEM** declarar `.AllowAnonymous()` explicitamente.
- Grupos de rotas:
  - `/api/v1/admin/**` → `RequireAuthorization(Policies.Admin)` no **grupo** (impossível esquecer em um endpoint).
  - `/api/v1/me/**`, `/api/v1/cart/**`, `/api/v1/orders/**` → `Policies.Customer`.
  - `/api/v1/catalog/**`, `/api/v1/shipping/**`, `/auth/register|login|refresh|forgot-password|reset-password|confirm-email`, `/health/*` → `AllowAnonymous`.
  - `/api/v1/webhooks/**` → `AllowAnonymous` + **validação de assinatura** da plataforma (filtro de endpoint) — não usam JWT.

## 3. Matriz de acesso

| Recurso / Ação | Visitante | Cliente | Admin |
|----------------|:---------:|:-------:|:-----:|
| Ver catálogo | ✅ | ✅ | ✅ |
| Cadastrar-se / logar | ✅ | — | — |
| Perfil e endereços próprios | ❌ | ✅ | — |
| Carrinho próprio | ❌ (local) | ✅ | ❌ |
| Simular frete | ✅ | ✅ | ✅ |
| Criar e pagar pedido | ❌ (cadastro obrigatório) | ✅ | ❌ |
| Ver/cancelar pedido próprio | ❌ | ✅ | — |
| Ver todos os pedidos / mudar status | ❌ | ❌ | ✅ |
| CRUD catálogo / estoque | ❌ | ❌ | ✅ |
| Gerenciar usuários | ❌ | ❌ | ✅ |

> Decidido: **não há compra sem conta** e **admin não compra com a mesma conta** (arquivo 20, Q06) — a policy `Customer` exclui `Admin`; quem administra usa outra conta para comprar.

## 4. Autorização por recurso (ownership)

- O `CustomerId` **NUNCA** vem do corpo/rota da requisição em endpoints de cliente: é obtido de `ICurrentUser.UserId` (claim `sub`).
- Queries filtram por dono no próprio SQL:
  ```sql
  SELECT ... FROM ordering.orders WHERE id = @OrderId AND customer_id = @CurrentUserId;
  ```
- Recurso de outro usuário → **`404 Not Found`** (não `403`), evitando enumeração de IDs.
- Testes funcionais **DEVEM** cobrir IDOR (cliente A tentando acessar pedido/endereço do cliente B).

## 5. Regras de negócio de autorização

- RN-090: Admin não pode desativar nem rebaixar a si mesmo.
- RN-091: Sempre ≥ 1 admin ativo (verificado em transação com `SELECT ... FOR UPDATE` na contagem).
- RN-092: Mudança de role/desativação invalida sessões (security stamp — arquivo 08).

## 6. `ICurrentUser`

```csharp
public interface ICurrentUser
{
    Guid UserId { get; }          // lança se não autenticado
    string Role { get; }
    bool IsAdmin { get; }
    bool IsAuthenticated { get; }
}
```
Implementado na Api a partir de `IHttpContextAccessor`; a Application depende só da interface.

## 7. Respostas

| Situação | HTTP |
|----------|------|
| Sem token / token inválido / expirado | `401` + `WWW-Authenticate: Bearer` |
| Autenticado sem o perfil necessário | `403` |
| Recurso de outro usuário | `404` |
