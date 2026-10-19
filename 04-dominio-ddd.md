# 04 — Domínio (DDD)

**Status:** Em revisão

## 1. Bounded Contexts

Monólito modular: cada contexto é uma pasta/namespace isolado no Domain e na Application. Contextos se comunicam por **IDs** (nunca referência direta a outro agregado) e por **domain events**.

```
┌──────────┐ UserRegistered ┌───────────┐                ┌───────────────┐
│ Identity │ ─────────────▶ │ Customers │                │ Notifications │ ◀── eventos (e-mails)
└──────────┘                └───────────┘                └───────────────┘
┌──────────┐                ┌───────────┐  quote  ┌──────────┐
│ Catalog  │ ◀─ VariantId ─ │   Carts   │ ──────▶ │ Shipping │
└────┬─────┘                └─────┬─────┘         └────┬─────┘
     │ VariantId                  │ checkout           │ quote
┌────▼──────┐ OrderPlaced/   ┌────▼─────┐ ◀────────────┘
│ Inventory │ ◀── Cancelled ─│ Ordering │
└───────────┘                └────┬─────┘
                                  │ OrderId        webhook
                             ┌────▼─────┐ ◀──────── plataforma
                             │ Payments │
                             └──────────┘
```

| Contexto | Responsabilidade | Agregados |
|----------|------------------|-----------|
| **Identity** | Credenciais, perfis, tokens, confirmação de e-mail, reset de senha | `User` |
| **Customers** | Dados do cliente e endereços | `Customer` (contém `Address`) |
| **Catalog** | Produtos, variações, categorias, cores, tamanhos | `Product` (contém `ProductVariant`, `ProductImage`), `Category`, `Color`, `Size` |
| **Inventory** | Quantidades e movimentações por variação | `StockItem` (contém `StockMovement`) |
| **Carts** | Carrinho do cliente | `Cart` (contém `CartItem`) |
| **Shipping** | Cotação de frete e regras comerciais | `ShippingRule`, `ShippingSettings` |
| **Ordering** | Pedidos e ciclo de vida | `Order` (contém `OrderItem`, `OrderStatusHistory`) |
| **Payments** | Pagamentos e estornos (arquivo 21) | `Payment` |
| **Notifications** | E-mails transacionais (arquivo 23) | — (serviço de aplicação) |

## 2. Blocos de construção (Abstractions)

```csharp
public abstract class Entity<TId> { public TId Id { get; protected init; } /* igualdade por Id */ }
public abstract class AggregateRoot<TId> : Entity<TId>
{
    private readonly List<IDomainEvent> _events = [];
    public IReadOnlyList<IDomainEvent> DomainEvents => _events;
    public int Version { get; protected set; }           // concorrência otimista
    protected void Raise(IDomainEvent e) => _events.Add(e);
    public void ClearDomainEvents() => _events.Clear();
}
public abstract record ValueObject;                       // records = igualdade estrutural
public sealed record Error(string Code, string Message, ErrorType Type);
public class Result / Result<T>                           // Success | Failure(Error)
```

Regras gerais:
- Entidades **DEVEM** proteger invariantes: setters privados, construção via *factory methods* que retornam `Result<T>`.
- Sem dependência de infraestrutura.
- Reidratação pelo Dapper via `static Rehydrate(...)`, usado **apenas** pela Infrastructure.
- IDs fortemente tipados: `readonly record struct ProductId(Guid Value)`, `VariantId`, `OrderId`...

## 3. Value Objects

| VO | Regras |
|----|--------|
| `Email` | formato válido, normalizado, máx. 254 |
| `Money` | `decimal` ≥ 0, 2 casas, BRL; `+`, `-`, `*` |
| `Sku` | 3–40 chars, `[A-Z0-9-]`, uppercase |
| `Slug` | sem acentos, `[a-z0-9-]` |
| `Cpf` | dígitos verificadores válidos |
| `Phone` | formato BR |
| `Quantity` | inteiro 1..99 |
| `PostalCode` | 8 dígitos |
| `Dimensions` | altura, largura, comprimento (cm) > 0 |
| `Weight` | gramas > 0 |
| `HexColor` | `#RRGGBB` |
| `Gtin` | código de barras EAN-8/EAN-13/GTIN-14, só dígitos, dígito verificador válido |
| `ShippingAddress` | snapshot imutável do endereço do pedido |
| `ShippingSelection` | snapshot: serviço, preço, prazo, se foi contingência |
| `SeoMetadata` | título ≤ 70, descrição ≤ 160 |

## 4. Agregados e invariantes

### 4.1 `User` (Identity)
- `Email`, `PasswordHash`, `Role`, `IsActive`, `EmailConfirmedAtUtc`, `FailedLoginAttempts`, `LockoutEndUtc`, `SecurityStamp`.
- RN-001 e-mail único. RN-002 lockout após 5 falhas. RN-003 troca de senha → novo `SecurityStamp`.
- Eventos: `UserRegistered`, `EmailConfirmed`, `PasswordResetRequested`, `PasswordChanged`, `UserRoleChanged`, `UserDeactivated`.

### 4.2 `Customer` (Customers)
- `Id` = Id do `User`. RN-010: até 10 endereços; exatamente 1 padrão.

### 4.3 `Category`, `Color`, `Size` (Catalog)
- Nomes e slugs únicos. `Size.SortOrder` define a ordem na grade.

### 4.4 `Product` (Catalog) — com variações
- `Id`, `Name`, `Slug`, `Description`, `CategoryId`, `BasePrice: Money`, `DefaultWeight`, `DefaultDimensions`, `Seo`, `Images`, `Variants`, `IsActive`.
- **`ProductVariant`** (entidade interna): `Id`, `Sku`, `Barcode?` (VO `Gtin`), `ColorId?`, `SizeId?`, `Price?` (nulo = `BasePrice`), `Weight?`, `Dimensions?`, `IsActive`.
- **`ProductImage`**: `Url`, `Alt`, `Position`, `ColorId?` (galeria muda conforme a cor escolhida).
- RN-020: preço efetivo > 0.
- RN-021: não pode haver duas variações com a mesma combinação cor + tamanho.
- RN-022: produto sem cor/tamanho tem **uma** variação "única" (mesmo modelo para todos os produtos).
- RN-023: produto ativo precisa de ≥ 1 variação ativa.
- RN-024: produto ou variação desativados não podem ser comprados.
- RN-025: alterar slug registra o slug antigo para redirect (SEO).
- RN-026: código de barras único em toda a loja (não pode repetir entre variações).
- Operação `GenerateVariants(colors, sizes)` cria a grade, sem duplicar as existentes.
- Eventos: `ProductCreated`, `ProductUpdated`, `ProductPriceChanged`, `ProductDeactivated`, `ProductSlugChanged` (cache e revalidação de páginas).

> Decisão: atributos **fixos** (cor e tamanho), não um sistema genérico de atributos — cobre o caso do cliente com muito menos complexidade. Se surgirem outros atributos (ex.: voltagem), evoluir para atributos genéricos.

### 4.5 `StockItem` (Inventory)
- Chave: **`VariantId`**. `QuantityOnHand` ≥ 0 (RN-030). Operações `Increase`, `Decrease`, `Adjust`, cada uma gera `StockMovement`.
- Baixa no checkout via **UPDATE condicional atômico** (arquivo 06).

### 4.6 `Cart` (Carts)
- Itens por **`VariantId`** + `Quantity`. RN-040: máx. 50 itens; 1..99 por item. Não armazena preço.

### 4.7 `ShippingRule` (Shipping)
- Condições (subtotal mínimo, UFs, faixas de CEP, serviços, vigência) e efeito (grátis, % desconto, desconto fixo, acréscimo). Ver arquivo 22.
- Domain service `ShippingPricingService`: aplica regras por prioridade sobre as cotações.

### 4.8 `Order` (Ordering)
- `Id`, `Number`, `CustomerId`, `Items` (snapshot: `VariantId`, `Sku`, `ProductName`, `ColorName`, `SizeName`, `UnitPrice`, `Quantity`), `ShippingAddress`, `ShippingSelection`, `Subtotal`, `ShippingFee`, `Total`, `Status`, `TrackingCode?`, `CancellationReason?`, `StatusHistory`, `PlacedAtUtc`, `PaymentDueAtUtc` (= `PlacedAtUtc` + 48 h).
- RN-050: ≥ 1 item. RN-051: total calculado pelo domínio. RN-052: transições conforme a máquina abaixo.
- RN-053: `CancellationReason` ∈ {`CustomerRequest`, `AdminRequest`, `PaymentTimeout`, `PaymentRejected`}.
- Eventos: `OrderPlaced`, `OrderPaymentConfirmed`, `OrderPreparationStarted`, `OrderShipped`, `OrderDelivered`, `OrderCancelled`.

#### Máquina de estados do pedido

```
                     ┌───────────────────────┐
                     │  AguardandoPagamento  │──── 48 h sem pagamento (sistema) ──┐
                     └──────────┬────────────┘                                    │
           webhook: aprovado     │        cliente/admin cancela                    ▼
                                ▼  ──────────────────────────────────────▶ ┌───────────┐
                   ┌─────────────────────┐   cliente/admin (+ estorno)     │ Cancelado │
                   │ PagamentoConfirmado │ ──────────────────────────────▶ └───────────┘
                   └──────────┬──────────┘                                       ▲
                              │ admin                                            │ admin (+ estorno)
                              ▼                                                  │
                       ┌─────────────┐ ──────────────────────────────────────────┘
                       │ EmSeparacao │
                       └──────┬──────┘
                              │ admin (+ código de rastreio)
                              ▼
                         ┌─────────┐  admin   ┌──────────┐
                         │ Enviado │ ───────▶ │ Entregue │
                         └─────────┘          └──────────┘
```

| De \ Para | PagamentoConfirmado | EmSeparacao | Enviado | Entregue | Cancelado |
|-----------|:---:|:---:|:---:|:---:|:---:|
| AguardandoPagamento | Webhook (admin, exceção com motivo) | — | — | — | Cliente / Admin / Sistema (48 h) |
| PagamentoConfirmado | — | Admin | — | — | Cliente / Admin (estorno) |
| EmSeparacao | — | — | Admin | — | Admin (estorno) |
| Enviado | — | — | — | Admin | — |

### 4.9 `Payment` (Payments)
- Ver arquivo 21. Invariante: no máximo um `Approved` por pedido; valor = total do pedido.

## 5. Domain Events e Outbox

- Eventos são coletados nos agregados. Efeitos **internos e atômicos** (baixa de estoque, criação do pagamento) acontecem na própria transação do caso de uso.
- Efeitos **externos** (e-mail, estorno na plataforma, revalidação de páginas) são gravados no **Outbox** na mesma transação e processados em background (arquivo 25).

## 6. Domain Services

- `OrderPricingService`: subtotal + frete.
- `ShippingPricingService`: regras de frete sobre as cotações.
- `CheckoutService` (Application): orquestra Cart → Shipping → Order → Inventory → Outbox.

## 7. Erros de domínio (catálogo inicial)

`Catalog.ProductNotFound`, `Catalog.VariantNotFound`, `Catalog.DuplicateVariant`, `Catalog.SkuAlreadyExists`, `Inventory.InsufficientStock`, `Shipping.QuoteUnavailable`, `Shipping.InvalidPostalCode`, `Ordering.InvalidStatusTransition`, `Ordering.EmptyCart`, `Ordering.PaymentWindowExpired`, `Payments.AlreadyPaid`, `Payments.AmountMismatch`, `Payments.InvalidSignature`, `Identity.InvalidCredentials`, `Identity.EmailAlreadyInUse`, `Identity.AccountLocked`, `Identity.EmailNotConfirmed`.
