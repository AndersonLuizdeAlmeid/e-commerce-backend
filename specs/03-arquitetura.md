# 03 — Arquitetura do Backend

**Status:** Em revisão

## 1. Estilo arquitetural

**Clean Architecture** + **DDD tático** + **CQRS leve** (commands e queries separados, mesmo banco), em um **monólito modular**.

```
            ┌───────────────────────────┐
            │          Api              │  Endpoints, middlewares, auth, CORS, rate limit
            └────────────┬──────────────┘
                         │ depende de
            ┌────────────▼──────────────┐
            │       Application         │  Casos de uso (handlers), DTOs, validação, portas
            └────────────┬──────────────┘
                         │ depende de
            ┌────────────▼──────────────┐
            │         Domain            │  Entidades, agregados, VOs, eventos, regras
            └───────────────────────────┘
            ┌───────────────────────────┐
            │      Infrastructure       │  Dapper, Npgsql, migrations, JWT, cache, relógio
            └───────────────────────────┘  implementa portas de Application/Domain
```

**Regra de dependência:** Domain não depende de nada. Application depende só de Domain. Infrastructure depende de Application/Domain. Api compõe tudo (composition root). Verificado por testes de arquitetura (NetArchTest).

## 2. Decisões (ADRs)

| ADR | Decisão | Motivo |
|-----|---------|--------|
| ADR-001 | .NET 10 (LTS) + ASP.NET Core **Minimal APIs** organizadas por feature (`IEndpointGroup`) | Menos cerimônia, ótimo desempenho, suporte LTS. |
| ADR-002 | **Dapper + Npgsql**, sem EF | Requisito do projeto; SQL explícito e controle total. |
| ADR-003 | Migrations em **SQL puro com DbUp** | Coerente com Dapper; scripts versionados e revisáveis. |
| ADR-004 | **Sem MediatR/AutoMapper** (licenças comerciais desde 2025). Dispatcher próprio simples (`ICommandHandler<TCmd,TResult>`, `IQueryHandler<TQuery,TResult>`) registrado via Scrutor + mapeamento manual (extension methods). | Evitar dependência paga e "mágica". |
| ADR-005 | **Result pattern** para falhas de negócio; exceções só para erros inesperados. | Fluxo explícito, sem exceções como controle de fluxo. |
| ADR-006 | IDs **UUID v7** (`Guid.CreateVersion7()`) gerados na aplicação | Ordenáveis (bom para índice B-tree), sem round-trip ao banco. |
| ADR-007 | Queries (leitura) podem ir **direto ao banco** retornando DTOs, sem passar por agregados | CQRS leve: leituras rápidas e simples. |
| ADR-008 | **FluentValidation** para validação de entrada | Licença Apache 2.0, maduro. |
| ADR-009 | **Central Package Management** (`Directory.Packages.props`) + `Directory.Build.props` (Nullable, TreatWarningsAsErrors, analyzers) | Padronização. |
| ADR-010 | Integrações externas (pagamento, Correios, e-mail) como **portas + adapters** (`IPaymentGateway`, `IShippingQuoteProvider`, `IEmailSender`) | Trocar de fornecedor sem mexer no domínio; testar com fakes/WireMock. |
| ADR-011 | **Outbox transacional** + `BackgroundService` para efeitos externos e jobs (arquivo 25) | Consistência entre banco e mundo externo sem transação distribuída. |
| ADR-012 | `HttpClient` tipado + `Microsoft.Extensions.Http.Resilience` (timeout, retry, circuit breaker) | Resiliência padronizada nas integrações. |

## 3. Estrutura da solution

```
e-commerce-backend/
├── specs/                               # este diretório
├── src/
│   ├── ECommerce.Domain/
│   │   ├── Abstractions/                # Entity, AggregateRoot, ValueObject, IDomainEvent, Result, Error
│   │   ├── Identity/                    # User, Role, RefreshToken, Email, PasswordHash
│   │   ├── Customers/                   # Customer, Address, Cpf, Phone
│   │   ├── Catalog/                     # Product, ProductVariant, Category, Color, Size, Sku, Slug, Money
│   │   ├── Inventory/                   # StockItem, StockMovement
│   │   ├── Carts/                       # Cart, CartItem
│   │   ├── Shipping/                    # ShippingRule, ShippingPricingService, IShippingQuoteProvider
│   │   ├── Ordering/                    # Order, OrderItem, OrderStatus, ShippingAddress
│   │   └── Payments/                    # Payment, IPaymentGateway
│   ├── ECommerce.Application/
│   │   ├── Abstractions/                # ICommandHandler, IQueryHandler, IUnitOfWork, IClock, ICurrentUser, ICacheService
│   │   ├── Behaviors/                   # decorators: validação, logging, transação
│   │   └── <Feature>/<UseCase>/         # Command/Query + Handler + Validator + Response
│   ├── ECommerce.Infrastructure/
│   │   ├── Persistence/                 # DbConnectionFactory, UnitOfWork, Repositories, TypeHandlers
│   │   ├── Persistence/Migrations/      # Scripts SQL (DbUp) — embedded resources
│   │   ├── Authentication/              # JwtTokenService, PasswordHasher
│   │   ├── Caching/                     # HybridCache
│   │   ├── Payments/<Provider>/         # adapter da plataforma de pagamento
│   │   ├── Shipping/Correios|MelhorEnvio/ # adapter de cotação de frete
│   │   ├── Email/                       # IEmailSender + templates
│   │   ├── Messaging/                   # Outbox, processador, jobs agendados
│   │   └── DependencyInjection.cs
│   └── ECommerce.Api/
│       ├── Endpoints/<Feature>/         # mapeamento de rotas
│       ├── Middlewares/                 # correlation id, exception handler
│       ├── Extensions/                  # Cors, RateLimiting, Auth, OpenApi
│       └── Program.cs
├── tests/
│   ├── ECommerce.Domain.UnitTests/
│   ├── ECommerce.Application.UnitTests/
│   ├── ECommerce.Infrastructure.IntegrationTests/
│   ├── ECommerce.Api.FunctionalTests/
│   └── ECommerce.ArchitectureTests/
├── docker-compose.yml
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
└── ECommerce.sln
```

**Vertical slice dentro da Application:** cada caso de uso fica em uma pasta própria, ex.: `Application/Ordering/PlaceOrder/{PlaceOrderCommand.cs, PlaceOrderHandler.cs, PlaceOrderValidator.cs, PlaceOrderResponse.cs}`.

## 4. Pipeline de um request (command)

```
HTTP → Middlewares (CorrelationId → ExceptionHandler → HTTPS/Headers → CORS → AuthN → RateLimiter → AuthZ)
     → Endpoint (bind + mapeia request → command)
     → Decorator Logging → Decorator Validation → Decorator Transaction (UoW)
     → Handler (carrega agregado via repositório → executa regra de domínio → persiste)
     → Commit → despacha domain events
     → Result → mapeado para HTTP (200/201/204 ou ProblemDetails)
```

Queries não passam pelo decorator transacional; usam `IDbConnectionFactory` diretamente e podem usar cache.

## 5. Pacotes principais (backend)

| Finalidade | Pacote |
|------------|--------|
| Acesso a dados | `Dapper`, `Npgsql` |
| Migrations | `dbup-postgresql` |
| Validação | `FluentValidation` |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.Extensions.Identity.Core` (somente `PasswordHasher<T>`) |
| Cache | `Microsoft.Extensions.Caching.Hybrid` (+ `StackExchangeRedis` opcional) |
| Logs | `Serilog.AspNetCore` |
| Telemetria | `OpenTelemetry.Extensions.Hosting` + instrumentações |
| Docs | `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` |
| DI scan | `Scrutor` |
| Health | `AspNetCore.HealthChecks.NpgSql` |
| Resiliência HTTP | `Microsoft.Extensions.Http.Resilience` |
| Pagamento | SDK oficial da plataforma escolhida (ex.: `mercadopago-sdk`) ou `HttpClient` tipado |
| E-mail | SDK do provedor (ex.: `AWSSDK.SimpleEmailV2`) ou `MailKit` (SMTP) + `Scriban`/Razor para templates |
| Testes de integração HTTP | `WireMock.Net` |
