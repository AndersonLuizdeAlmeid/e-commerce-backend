# 15 — Estratégia de Testes

**Status:** Em revisão

## 1. Pirâmide

```
            ▲   E2E (Playwright — frontend + API)          poucos, fluxos críticos
           ▲▲▲  Funcionais da API (WebApplicationFactory + Postgres real)
         ▲▲▲▲▲  Integração (repositórios/queries Dapper + Testcontainers)
      ▲▲▲▲▲▲▲▲▲ Unitários (Domain + Application)          muitos, rápidos
      ─────────  + Testes de arquitetura (NetArchTest)
```

## 2. Projetos e ferramentas (backend)

| Projeto | Escopo | Ferramentas |
|---------|--------|-------------|
| `ECommerce.Domain.UnitTests` | Agregados, VOs, máquina de estados, invariantes | xUnit v3, Shouldly* |
| `ECommerce.Application.UnitTests` | Handlers (com repositórios fakes/mocks), validators, decorators | xUnit v3, NSubstitute, Shouldly, Bogus |
| `ECommerce.Infrastructure.IntegrationTests` | Repositórios, queries, migrations, concorrência de estoque, outbox, adapters de pagamento/frete/e-mail | **Testcontainers.PostgreSql**, Respawn, **WireMock.Net** |
| `ECommerce.Api.FunctionalTests` | Endpoints ponta a ponta: auth, autorização, CORS, rate limit, ProblemDetails | `WebApplicationFactory`, Testcontainers, Respawn |
| `ECommerce.ArchitectureTests` | Regras de dependência entre camadas, convenções de nomes | NetArchTest.Rules |

\* `FluentAssertions` v8+ é comercial; usaremos **Shouldly** (ou o fork open-source **AwesomeAssertions**).

## 3. Convenções

- Nome: `Metodo_Cenario_ResultadoEsperado` (ex.: `Cancel_WhenOrderIsShipped_ReturnsInvalidTransition`).
- Padrão **Arrange / Act / Assert**; um comportamento por teste.
- **Builders/Object Mothers** para criar agregados válidos (`ProductBuilder`, `OrderBuilder`).
- Tempo controlado via `TimeProvider` (`FakeTimeProvider`) — nunca `DateTime.UtcNow` direto no código.
- Testes de integração/funcionais compartilham **um container por coleção** (`ICollectionFixture`) e limpam dados com **Respawn** entre testes.
- Migrations são aplicadas no container pelo mesmo DbUp de produção (testa as migrations também).

## 4. Rastreabilidade spec → teste

Cada critério de aceite (`RF-xxx CAy`) tem ao menos um teste. Usar trait:
```csharp
[Fact, Trait("Spec", "RF-040-CA2")]
public async Task PlaceOrder_WhenAnyItemOutOfStock_ReturnsConflictAndPersistsNothing() { ... }
```

## 5. Casos obrigatórios (não negociáveis)

- **Concorrência de estoque**: N checkouts paralelos disputando a última unidade → exatamente 1 sucesso, estoque final = 0, nunca negativo.
- **Idempotência**: dois `POST /orders` com mesma `Idempotency-Key` → 1 pedido.
- **IDOR**: cliente A não acessa pedido/endereço do cliente B (404).
- **Autorização**: cliente recebe 403 em todo `/admin/**` (teste que varre todos os endpoints do grupo).
- **Endpoints sem `AllowAnonymous` exigem token** (teste que varre `EndpointDataSource`).
- **Refresh token**: rotação e detecção de reuso.
- **Máquina de estados do pedido**: todas as transições válidas e inválidas (teste parametrizado).
- **Rate limit** de login e **CORS** (origem permitida e negada).
- **Cache**: invalidação após alteração de produto.
- **Variações**: grade sem duplicidade; estoque e carrinho por variação; pedido congela cor/tamanho.
- **Pagamentos**: webhook com assinatura inválida, duplicado, fora de ordem; pagamento aprovado após cancelamento → estorno; valor divergente rejeitado.
- **Cancelamento em 48 h**: com `FakeTimeProvider`; corrida com webhook de aprovação.
- **Frete**: regras (prioridade, vigência, região, valor mínimo); contingência quando a API cai; recálculo no checkout.
- **E-mails**: e-mail só sai após commit (rollback não envia); Mailpit nos testes funcionais.
- **SEO** (frontend): metadata, canonical, JSON-LD e status 301/404/410 nas páginas de produto.

## 6. Testes de arquitetura (exemplos)

- Domain não referencia `Dapper`, `Npgsql`, `Microsoft.AspNetCore.*`.
- Application não referencia Infrastructure nem Api.
- Handlers são `sealed` e terminam com `Handler`.
- Entidades de domínio não têm setters públicos.

## 7. Cobertura e CI

- Coverlet + ReportGenerator. Metas: Domain ≥ 90%, Application ≥ 80% (gate no CI).
- Todos os testes rodam no CI (GitHub Actions possui Docker para Testcontainers).
- Testes de mutação (Stryker.NET) no Domain — opcional, periódico.

## 8. Frontend

Ver arquivo 16: Vitest + Testing Library + MSW (unit/componentes), Playwright (E2E, incluindo pagamento no sandbox da plataforma) e Lighthouse CI.
