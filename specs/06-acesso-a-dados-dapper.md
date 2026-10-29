# 06 — Acesso a Dados com Dapper

**Status:** Em revisão

## 1. Princípios

- **Sem Entity Framework.** SQL explícito, escrito à mão, revisado em PR.
- **Sempre parametrizado** (`@param`). Concatenação de string em SQL é proibida (exceção: nomes de colunas de ordenação vindos de *whitelist*).
- Nada de `SELECT *`: colunas explícitas com alias para o nome da propriedade quando necessário.
- Todas as chamadas são **assíncronas** e recebem `CancellationToken` via `CommandDefinition`.

## 2. Componentes

```csharp
// Application/Abstractions
public interface IDbConnectionFactory { ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct); }
public interface IUnitOfWork
{
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
    Task BeginAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}
```

- `NpgsqlDataSource` registrado como **singleton** (pool de conexões gerenciado pelo Npgsql).
- `UnitOfWork` é **scoped** (um por request). Repositórios recebem o `IUnitOfWork` e usam sua conexão/transação.
- **Decorator transacional** abre a transação antes do handler de command e faz commit/rollback ao final; depois do commit despacha os domain events.

## 3. Repositórios (lado de escrita)

- **Um repositório por agregado** (não por tabela): `IProductRepository`, `IOrderRepository`, `ICartRepository`, `IUserRepository`, `IStockRepository`, `ICustomerRepository`, `IPaymentRepository`, `IShippingRuleRepository`.
- Interfaces no **Domain/Application**; implementações na **Infrastructure**.
- Métodos típicos: `GetByIdAsync`, `AddAsync`, `UpdateAsync`, `ExistsBySkuAsync`.
- Carregamento de agregado com filhos via `QueryMultipleAsync` (um round-trip) ou *multi-mapping* (`splitOn`).
- Persistência de coleções filhas: estratégia *diff* (inserir novos, atualizar alterados, remover ausentes) ou *delete+insert* para coleções pequenas (ex.: imagens).

## 4. Queries (lado de leitura)

- Handlers de query usam `IDbConnectionFactory` diretamente e mapeiam para **DTOs de leitura** (records), sem reidratar agregados.
- Paginação com `LIMIT/OFFSET` no MVP + `COUNT(*) OVER()` para total; **keyset pagination** para listas grandes (admin de pedidos) em evolução futura.
- Ordenação dinâmica via whitelist: `{"price" => "p.price", "name" => "p.name", "createdAt" => "p.created_at"}`.

## 5. Configuração do Dapper

```csharp
DefaultTypeMap.MatchNamesWithUnderscores = true;   // snake_case → PascalCase
SqlMapper.AddTypeHandler(new ProductIdTypeHandler()); // um por ID fortemente tipado
SqlMapper.AddTypeHandler(new MoneyTypeHandler());
SqlMapper.AddTypeHandler(new DateTimeUtcHandler());   // garante DateTimeKind.Utc
```

- Value objects complexos são mapeados manualmente em "rows" internas (`ProductRow`) e convertidos para o agregado via `Product.Rehydrate(...)`.

## 6. Concorrência

### 6.1 Otimista (agregados)
```sql
UPDATE catalog.products
   SET name = @Name, price = @Price, ..., version = version + 1, updated_at = @Now
 WHERE id = @Id AND version = @Version;
```
`rowsAffected == 0` → `ConcurrencyConflict` → HTTP `409`.

### 6.2 Baixa de estoque no checkout (crítico)
Atômica e sem read-modify-write:
```sql
UPDATE inventory.stock_items
   SET quantity_on_hand = quantity_on_hand - @Qty, version = version + 1, updated_at = @Now
 WHERE variant_id = @VariantId AND quantity_on_hand >= @Qty;
```
- Se `rowsAffected == 0` para qualquer item → `Inventory.InsufficientStock` → **rollback** de toda a transação.
- Itens processados **ordenados por `variant_id`** para evitar deadlocks entre checkouts concorrentes.
- `CHECK (quantity_on_hand >= 0)` é a última barreira.

### 6.3 Idempotência do checkout
- `UNIQUE (customer_id, idempotency_key)`. Em violação (`23505`), retorna o pedido já existente com `200`.

## 7. Tratamento de erros do Npgsql

| SQLSTATE | Significado | Tradução |
|----------|-------------|----------|
| `23505` | unique_violation | erro de conflito de domínio (ex.: `SkuAlreadyExists`) → 409 |
| `23503` | foreign_key_violation | `NotFound`/`Conflict` conforme contexto |
| `23514` | check_violation | erro de validação/estado → 422 |
| `40001`/`40P01` | serialization/deadlock | **retry** (até 3x, backoff) no decorator transacional |

## 8. Outbox e pagamentos

- O Outbox é gravado com a **mesma conexão e transação** do `IUnitOfWork` (arquivo 25).
- Pagamento aprovado e mudança de status do pedido acontecem na mesma transação; o cancelamento automático usa `UPDATE ... WHERE status = 'PendingPayment' AND version = @Version` para não colidir com um webhook simultâneo.

## 9. Testes

- Repositórios e queries testados **contra PostgreSQL real** com Testcontainers (arquivo 15) — nunca com mocks de `IDbConnection`.
