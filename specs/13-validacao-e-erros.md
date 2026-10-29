# 13 — Validação e Tratamento de Erros

**Status:** Rascunho

## 1. Camadas de validação

| Camada | O que valida | Como | Resultado |
|--------|--------------|------|-----------|
| **Api** | Formato/binding (JSON malformado, tipos) | Model binding do ASP.NET | `400` |
| **Application** | Regras de entrada (obrigatório, tamanho, formato, faixa) | **FluentValidation** via decorator | `400` com erros por campo |
| **Domain** | Invariantes e regras de negócio | Factory methods / métodos do agregado → `Result` | `409`/`422`/`404` |
| **Banco** | Última barreira (UNIQUE, CHECK, FK) | Constraints | traduzido (arquivo 06) |

## 2. Result pattern

```csharp
public enum ErrorType { Validation, NotFound, Conflict, BusinessRule, Unauthorized, Forbidden }

public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error NotFound(string code, string msg) => new(code, msg, ErrorType.NotFound);
    // ...
}

public class Result<T> { public bool IsSuccess; public T Value; public Error Error; }
```

- Handlers retornam `Result`/`Result<T>`; **não** lançam exceção para falha de negócio.
- Exceções ficam para situações **inesperadas** (banco fora, bug).

## 3. Mapeamento para HTTP

| `ErrorType` | HTTP |
|-------------|------|
| `Validation` | `400 Bad Request` |
| `Unauthorized` | `401` |
| `Forbidden` | `403` |
| `NotFound` | `404` |
| `Conflict` (unicidade, concorrência, estoque) | `409` |
| `BusinessRule` (transição inválida, carrinho vazio) | `422 Unprocessable Entity` |
| Exceção não tratada | `500` (sem detalhes internos) |
| Rate limit | `429` |

## 4. Formato — ProblemDetails (RFC 9457)

```json
{
  "type": "https://api.loja.exemplo.com/errors/inventory.insufficient-stock",
  "title": "Estoque insuficiente",
  "status": 409,
  "detail": "Um ou mais itens não possuem estoque suficiente.",
  "instance": "/api/v1/orders",
  "code": "Inventory.InsufficientStock",
  "traceId": "00-4bf92f...-01",
  "errors": {
    "items[0].quantity": ["Disponível: 1"]
  }
}
```

- `code` é estável e usado pelo frontend para lógica/tradução; `title`/`detail` em pt-BR.
- `traceId` sempre presente (correlação com logs).
- Implementação: `builder.Services.AddProblemDetails(...)` + `IExceptionHandler` global (`GlobalExceptionHandler`) + extension `result.ToHttpResult()` nos endpoints.

## 5. Regras

- **Nunca** vazar stack trace, SQL ou mensagens de exceção em produção.
- Mensagens de autenticação **genéricas** (não revelar se e-mail existe).
- Exceções não tratadas logadas em `Error` com contexto (rota, userId, correlationId).
- Validators **DEVEM** ter testes unitários.

## 6. Exemplo de validator

```csharp
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.AddressId).NotEmpty();
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}
```
