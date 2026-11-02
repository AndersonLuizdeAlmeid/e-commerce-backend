# 07 — Contratos da API

**Status:** Em revisão

## 1. Convenções

- Base: `/api/v1`. Versionamento por URL.
- JSON `camelCase`, enums como **string**, datas ISO-8601 UTC, dinheiro como número decimal com 2 casas.
- Erros: `application/problem+json` (RFC 9457) — arquivo 13.
- Paginação: `?page=1&pageSize=20` → `{ "items": [], "page": 1, "pageSize": 20, "totalItems": 135, "totalPages": 7 }`.
- `POST` que cria recurso → `201` + `Location`.
- Headers: `Authorization: Bearer <jwt>`, `Idempotency-Key` (checkout e pagamento), `X-Correlation-Id`.
- OpenAPI em `/openapi/v1.json`; UI Scalar em `/docs` (fora de produção). Tipos do frontend são gerados a partir dele.

Legenda: 🌐 público · 👤 Cliente · 🛡️ Admin · 🔑 qualquer autenticado · 🔗 plataforma externa (assinatura)

## 2. Auth — `/api/v1/auth`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| POST | `/register` | 🌐 | Cadastro (RF-001) — já retorna tokens |
| POST | `/login` | 🌐 | Login (RF-002) |
| POST | `/refresh` | 🌐 (cookie) | Renova tokens (RF-003) |
| POST | `/logout` | 🌐 (cookie) | Logout (RF-004) — funciona com o access token vencido; exige `X-Requested-With` |
| POST | `/change-password` | 🔑 | Altera senha (RF-006) |
| POST | `/forgot-password` | 🌐 | `{ email }` → sempre 202 (RF-008) |
| POST | `/reset-password` | 🌐 | `{ token, newPassword }` (RF-008) |
| POST | `/confirm-email` | 🌐 | `{ token }` (RF-009) |
| POST | `/resend-confirmation` | 🔑 | Reenvia o e-mail de confirmação |
| GET | `/me` | 🔑 | id, e-mail, role, `emailConfirmed` |

## 3. Conta do cliente — `/api/v1/me`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| GET/PUT | `/profile` | 👤 | Perfil (RF-005) |
| GET/POST | `/addresses` | 👤 | Listar/criar (RF-007) |
| PUT/DELETE | `/addresses/{id}` | 👤 | Atualizar/remover |
| PUT | `/addresses/{id}/default` | 👤 | Definir padrão |

## 4. Catálogo — `/api/v1/catalog`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| GET | `/products?page&pageSize&category&minPrice&maxPrice&search&colors&sizes&sort=price:asc` | 🌐 | RF-020 |
| GET | `/products/{slug}` | 🌐 | RF-021 — inclui variações, grade e imagens; slug antigo → `301` com `Location`; inativo → `410` |
| GET | `/categories` | 🌐 | RF-022 — inclui `parentId`; a navegação da loja mostra só as principais |
| GET | `/categories/{slug}` | 🌐 | Dados da categoria (texto, SEO, `variantLabels`); slug antigo → `301` com `Location` |
| GET | `/filters?category=` | 🌐 | Cores e tamanhos disponíveis (RF-023) |
| GET | `/collections` | 🌐 | Coleções ativas, na ordem da navegação (RF-024) |
| GET | `/collections/{slug}` | 🌐 | Dados da coleção (texto, SEO, capa, `cardTheme`); slug antigo → `301` |
| GET | `/showcase` | 🌐 | `{ featured: { product, colorId?, isFallback }, occasion: { name, text, image?, products[] } \| null }` (RF-025) |
| GET | `/sitemap` | 🌐 | Slugs + `updatedAt` de produtos e categorias ativos (consumido pelo Next.js) |
| GET | `/feeds/google-merchant.xml` | 🌐 | Feed de produtos (SEO-14) |

Detalhes implementados na Fase 1:

- **`GET /products`**: `colors` são **slugs** de cor e `sizes` **nomes** de tamanho, repetindo o parâmetro (`colors=preto&colors=azul`). Um produto só entra quando **uma mesma variação ativa** atende à cor e ao tamanho pedidos. `sort` ∈ `createdAt:desc` (padrão), `createdAt:asc`, `price:asc`, `price:desc`, `name:asc`, `name:desc`; o preço usado em filtro e ordenação é o "a partir de". Padrões: `page=1`, `pageSize=20`. Entrada inválida (`pageSize` fora de 1–100, `page` < 1, `maxPrice` < `minPrice`, `sort` fora da lista, `search` > 100 caracteres, mais de 20 cores/tamanhos) → `400` com `code: "Validation.Failed"` e `errors` por campo. Página além do fim → `items: []` com o `totalItems` real.
- Item da listagem: `{ id, slug, name, categorySlug, priceFrom, colors: [{ id, name, slug, hex }], image: { url, alt, colorId } | null }`.
- **`GET /products/{slug}`**: slug antigo → `301` com `Location: /api/v1/catalog/products/{slugAtual}`; produto inativo, sem variação ativa ou em categoria inativa → `410` (`code: "Catalog.ProductUnavailable"`, extensão `categorySlug`, nula se a categoria também saiu); slug desconhecido → `404` (`Catalog.ProductNotFound`). As variações trazem `gtin` (código de barras, para o JSON-LD) e `available`, nunca a quantidade.
- **`GET /filters`** → `{ colors, sizes, minPrice, maxPrice }` considerando só variações visíveis na loja.
- **`GET /sitemap`** → `{ categories: [{ slug, updatedAt }], products: [{ slug, updatedAt }] }`.
- *A partir da Fase 2B (arquivo 26):* `GET /products` e `GET /filters` aceitam `collection` (slug); `GET /filters` traz `categories`; `GET /categories/{slug}` e `GET /products/{slug}` trazem `variantLabels: { color, size }` já resolvidos (RF-050); `GET /sitemap` inclui `collections`.
- Todos os `GET` públicos do catálogo respondem `Cache-Control: public, max-age=60` e `ETag`; `If-None-Match` igual → `304` sem corpo. Erros não recebem esses headers.

Exemplo `GET /products/camiseta-basica`:
```json
{
  "id": "…", "slug": "camiseta-basica", "name": "Camiseta Básica", "description": "…",
  "category": { "slug": "camisetas", "name": "Camisetas" },
  "priceFrom": 59.90,
  "seo": { "title": "Camiseta Básica de Algodão", "description": "…" },
  "colors": [ { "id": "…", "name": "Azul", "hex": "#1B3A6B" }, { "id": "…", "name": "Preto", "hex": "#151515" } ],
  "sizes":  [ { "id": "…", "name": "P" }, { "id": "…", "name": "M" }, { "id": "…", "name": "G" } ],
  "variants": [
    { "id": "…", "sku": "CAM-BAS-AZU-M", "colorId": "…", "sizeId": "…", "price": 59.90, "gtin": "7891234567895", "available": true }
  ],
  "images": [ { "url": "…", "alt": "Camiseta básica azul, frente", "colorId": "…" } ]
}
```

## 5. Frete — `/api/v1/shipping`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| POST | `/quotes` | 🌐 | `{ postalCode, items: [{ variantId, quantity }] }` → opções (RF-032). CEP inválido → `400`; peça fora da loja → `422`; loja sem configuração → `503` `Shipping.NotConfigured` |
| GET | `/postal-codes/{cep}` | 🌐 | Endereço pelo CEP (proxy ViaCEP com cache). CEP inexistente → `404`; serviço fora do ar → `503` |

Resposta:
```json
{ "options": [
  { "serviceCode": "PAC",   "name": "PAC",   "price": 0.00,  "originalPrice": 21.40, "deliveryDays": 7, "appliedRules": ["Frete grátis acima de R$ 299"] },
  { "serviceCode": "SEDEX", "name": "SEDEX", "price": 38.90, "originalPrice": 38.90, "deliveryDays": 3, "appliedRules": [] }
], "isFallback": false, "freeShippingRemaining": null }
```

`deliveryDays` já soma os dias de manuseio. Com o provedor fora do ar, `isFallback` é verdadeiro, os preços partem da contingência e `deliveryDays` é nulo. `freeShippingRemaining` é quanto falta para o frete grátis, quando há regra para esse cliente e nenhum serviço já está grátis.

## 6. Carrinho — `/api/v1/cart`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| GET | `/` | 👤 | Carrinho com preços atuais e alertas de estoque |
| POST | `/items` | 👤 | `{ variantId, quantity }`; se a peça já está na sacola, soma. Devolve a sacola |
| PUT | `/items/{variantId}` | 👤 | `{ quantity }`. Devolve a sacola |
| DELETE | `/items/{variantId}` | 👤 | Remove item. Devolve a sacola |
| DELETE | `/` | 👤 | Limpa |
| POST | `/merge` | 👤 | `{ items: [{ variantId, quantity }] }` (RF-031) → `{ cart, skippedVariantIds }` |
| POST | `/api/v1/catalog/variants/lookup` | 🌐 | `{ items: [{ variantId, quantity }] }`, até 50: a sacola do visitante, que só guarda variação e quantidade. Mesma resposta de `GET /`, sem cache; variação inexistente fica de fora |

Resposta: `{ items: [{ variantId, productName, productSlug, colorName, sizeName, variantLabels, imageUrl, imageAlt, unitPrice, quantity, lineTotal, status, availableQuantity }], subtotal }`. `status`: `Available`, `InsufficientStock` (com `availableQuantity`), `OutOfStock` ou `Unavailable`. `subtotal` conta só o que pode ser comprado. Variação inativa ou inexistente em `POST /items` → `422` `Carts.VariantUnavailable`; passar de 99 por peça ou 50 peças diferentes → `422`.

## 7. Pedidos e pagamentos (cliente) — `/api/v1/orders`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| POST | `/` | 👤 | Checkout `{ addressId, shippingServiceCode, notes?, gift?: { wrap, message? } }` + `Idempotency-Key` (RF-040, RF-047) → 201 |
| GET | `/` | 👤 | Meus pedidos (RF-041) |
| GET | `/{id}` | 👤 | Detalhe com pagamento, frete, rastreio (RF-042) |
| POST | `/{id}/cancel` | 👤 | Cancelar (RF-043) |
| POST | `/{id}/payments` | 👤 | `{ method: Pix\|CreditCard\|Boleto, cardToken?, installments? }` + `Idempotency-Key` (RF-044) |
| GET | `/{id}/payments/current` | 👤 | Status do pagamento (o frontend consulta enquanto mostra o QR PIX) |

Exemplo `POST /orders/{id}/payments` (PIX) → `201`:
```json
{ "paymentId": "…", "status": "Pending", "method": "Pix",
  "pix": { "qrCodeBase64": "…", "copyPaste": "000201…", "expiresAt": "2026-10-21T13:00:00Z" } }
```

## 8. Webhooks — `/api/v1/webhooks`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| POST | `/payments/{provider}` | 🔗 | Notificação da plataforma (RF-045) — valida assinatura, sem CORS, responde 200 rápido |

## 9. Backoffice — `/api/v1/admin` (🛡️ todos)

| Método | Rota | Descrição |
|--------|------|-----------|
| GET/POST | `/categories` | Listar / criar (RF-050) |
| PUT | `/categories/{id}` | Editar |
| PATCH | `/categories/{id}/status` | Ativar/desativar |
| DELETE | `/categories/{id}` | Exclui; com produtos ou subcategorias → `409` |
| GET/POST | `/colors`, `/sizes` | Cores e tamanhos (RF-057) |
| PUT/DELETE | `/colors/{id}`, `/sizes/{id}` | Editar/remover (se não usado) |
| GET/POST | `/products` | Listar / criar (RF-051) |
| GET/PUT | `/products/{id}` | Detalhe / editar |
| PATCH | `/products/{id}/status` | Ativar/desativar |
| POST | `/products/{id}/variants/generate` | `{ colorIds[], sizeIds[] }` cria só as combinações que faltam e devolve `{ created }` (RF-056); listas vazias criam a variação única |
| PUT | `/products/{id}/variants/{variantId}` | Editar variação |
| PATCH | `/products/{id}/variants/{variantId}/status` | Ativar/desativar |
| POST | `/products/{id}/images` | Upload multipart de **um** arquivo (máx. 15 MB) com `alt` e `colorId?` (RF-061) → 201 com `{ id, url, width, height }`; o painel envia até 3 em paralelo |
| PUT/DELETE | `/products/{id}/images/{imageId}` | `{ alt, colorId? }` / remover (a última foto de produto ativo → 422) |
| PUT | `/products/{id}/images/order` | `{ colorId?, imageIds[] }` reordena a galeria de um acabamento |
| GET/POST | `/collections` | Listar / criar (RF-024) |
| PUT/DELETE | `/collections/{id}` | Editar / excluir (só os vínculos saem) |
| PATCH | `/collections/{id}/status` | Ativar/desativar |
| POST/DELETE | `/collections/{id}/products` | `{ productIds[] }` inclui (`{ added }`) / remove em lote, até 100 |
| PUT/DELETE | `/collections/{id}/image` | Capa da coleção (multipart, RF-061) / remover |
| GET/PUT | `/showcase/featured` | Peça em destaque (RF-025). GET: `{ productId, productName, colorId, isAvailable }`, ou 204 sem escolha. PUT: `{ productId?, colorId? }`; `productId` nulo tira o destaque |
| GET/POST | `/occasions` | Ocasiões (RF-025): `{ name, text?, startsOn, endsOn, isActive, productIds[] }`, datas em `AAAA-MM-DD` |
| PUT/DELETE | `/occasions/{id}` | Editar (`{ version, occasion }`) / excluir; período sobreposto → `409` |
| PUT/DELETE | `/occasions/{id}/image` | Imagem da ocasião (multipart, RF-061) / remover |
| PUT | `/orders/{id}/gift-message` | Corrigir dedicatória até `Enviado` (RF-047 CA3) |
| GET | `/variants/by-barcode/{code}` | Localiza a variação (RF-059); dígito errado → `400`; sem dono → `404` `Catalog.BarcodeNotFound` |
| POST | `/stock/scan` | `{ barcode, quantity = 1, reason? }` entrada de estoque por leitura; cada chamada soma de novo. Devolve a peça e o saldo. Dígito errado → `400`; sem dono → `404` `Catalog.BarcodeNotFound` (RF-059) |
| GET | `/stock?lowStock=true&search=&page=` | Estoque por variação (busca também por código de barras). `lowStock` traz só o que está à venda |
| POST | `/stock/{variantId}/movements` | `{ type: In\|Out\|Adjustment, quantity, reason }`: em `In` e `Out`, o que entra ou sai; em `Adjustment`, o novo saldo. Saldo negativo → `422` `Inventory.InsufficientStock` (RF-052) |
| GET | `/stock/{variantId}/movements?page=` | Histórico: tipo, delta com sinal, motivo, autor e data |
| GET/PUT | `/shipping/settings` | CEP de origem, manuseio, contingência e caixa padrão (RF-058). GET responde `204` enquanto não configurado |
| GET/POST | `/shipping/rules` | Regras de frete: `{ name, priority, isActive, validFrom?, validTo?, minSubtotal?, states[], postalCodeRanges: [{ from, to }], serviceCodes[], effect, effectValue? }`. Condição vazia vale para todos |
| PUT/DELETE | `/shipping/rules/{id}` | Editar/remover |
| GET | `/orders?status&from&to&customerId&paymentMethod&page` | Pedidos (RF-053) |
| GET | `/orders/{id}` | Detalhe com histórico e pagamentos |
| POST | `/orders/{id}/confirm-payment` | Exceção manual, `{ reason }` obrigatório |
| POST | `/orders/{id}/start-preparation` | → EmSeparacao |
| POST | `/orders/{id}/ship` | `{ trackingCode }` → Enviado |
| POST | `/orders/{id}/deliver` | → Entregue |
| POST | `/orders/{id}/cancel` | `{ reason }` → Cancelado (+ estorno) |
| GET | `/payments/refunds/pending` | Estornos com falha para tratamento |
| GET | `/users?role&isActive&search&page` | Usuários (RF-054) |
| PATCH | `/users/{id}/status`, `/users/{id}/role` | Ativar/desativar, mudar perfil |
| GET | `/dashboard?from&to` | Indicadores (RF-055) |

## 10. Infra

| Rota | Acesso | Descrição |
|------|--------|-----------|
| `/health/live` | 🌐 | Processo vivo |
| `/health/ready` | 🌐 (rede interna) | Banco acessível |
