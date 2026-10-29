# 12 — Cache

**Status:** Em revisão

## 1. Precisamos de cache?

**Sim, mas de forma pontual.** O catálogo é lido muito mais do que escrito (alta razão leitura/escrita) e é público — é o principal candidato. Dados transacionais (carrinho, pedidos, estoque exato) **não** são cacheados para evitar inconsistências.

## 2. Tecnologia

- **`HybridCache`** (`Microsoft.Extensions.Caching.Hybrid`, .NET 9+):
  - L1 em memória por instância + L2 distribuído **opcional** (Redis) quando houver mais de uma instância.
  - Proteção contra *cache stampede* nativa (uma única execução da factory por chave).
  - Invalidação por **tags**.
- Abstração na Application: `ICacheService` (ou uso direto de `HybridCache` atrás de interface), para os handlers não dependerem da implementação.

## 3. O que cachear

| Dado | Chave | Tags | TTL | Invalidação |
|------|-------|------|-----|-------------|
| Lista de categorias ativas | `catalog:categories` | `categories` | 30 min | Criar/editar/ativar categoria |
| Detalhe de produto | `catalog:product:{idOrSlug}` | `products`, `product:{id}` | 10 min | Editar produto/preço/status/imagens |
| Listagem de produtos (por combinação de filtros) | `catalog:products:{hash(filtros)}` | `products` | 2 min | Qualquer escrita em produto |
| Disponibilidade (`inStock`) | — | — | **não cacheado** no detalhe; na listagem tolera até 2 min de atraso | — |
| Security stamp do usuário | `identity:sstamp:{userId}` | `user:{id}` | 1 min | Troca de senha / role / desativação |
| Dashboard admin | `admin:dashboard:{from}:{to}` | `dashboard` | 1 min | Expiração |
| Cotação de frete | `shipping:quote:{cepDestino}:{hash(pacotes)}` | `shipping` | 30 min | Alteração de regras/configuração de frete |
| Regras de frete ativas | `shipping:rules` | `shipping` | 10 min | CRUD de regras |
| Endereço por CEP | `shipping:cep:{cep}` | — | 7 dias | Expiração |
| Cores e tamanhos | `catalog:filters:{category}` | `products` | 10 min | Escrita em produto/variação/cor/tamanho |

**Não cachear:** carrinho, pedidos, endereços, estoque no checkout (sempre leitura transacional), respostas autenticadas no HTTP.

## 4. Invalidação

- Handlers de comandos administrativos (ou handlers dos eventos `ProductCreated`, `ProductPriceChanged`, `ProductDeactivated`, etc.) chamam `RemoveByTagAsync(...)` **após o commit**.
- Preferir invalidar por tag ampla (`products`) a calcular chaves específicas — simplicidade > precisão no MVP.

## 4b. Cache das páginas (Next.js)

- Páginas públicas usam **ISR**: o Next.js guarda o HTML gerado e o revalida.
- Quando o admin altera catálogo/estoque, o backend dispara (via Outbox) a **revalidação sob demanda** das páginas afetadas (arquivo 24 §3), além de invalidar o HybridCache.

## 5. Cache HTTP

- Endpoints públicos de catálogo retornam `Cache-Control: public, max-age=60` e `ETag` (hash do conteúdo/`version`) → `304 Not Modified` em `If-None-Match`.
- Endpoints autenticados: `Cache-Control: no-store`.
- *Output caching* do ASP.NET Core **não** será usado inicialmente (duplicaria o HybridCache); reavaliar se houver necessidade.
- Implementação (Fase 1): endpoint filter `WithPublicHttpCache()` — só em respostas `200`; o `ETag` é o SHA-256 do JSON serializado, então muda quando muda o conteúdo (incluindo disponibilidade).
- O detalhe do produto fica no HybridCache **sem** a disponibilidade; ela é consultada a cada request e mesclada (linha "Disponibilidade" acima).

## 6. Configuração

```json
"Caching": {
  "UseRedis": false,
  "Redis": { "ConnectionString": "" },
  "DefaultExpirationMinutes": 5
}
```

## 7. Critérios de aceite

- **CA1** Segunda leitura de `GET /catalog/categories` não consulta o banco (verificável por métrica/teste).
- **CA2** Após `PUT /admin/products/{id}` alterar o preço, `GET /catalog/products/{id}` retorna o preço novo imediatamente (na mesma instância).
- **CA3** Falha do Redis (quando habilitado) **não** derruba a API — degrada para L1/banco.
