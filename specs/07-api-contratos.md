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
| GET | `/categories` | 🌐 | RF-022 |
| GET | `/categories/{slug}` | 🌐 | Dados da categoria (texto, SEO) |
| GET | `/filters?category=` | 🌐 | Cores e tamanhos disponíveis (RF-023) |
| GET | `/sitemap` | 🌐 | Slugs + `updatedAt` de produtos e categorias ativos (consumido pelo Next.js) |
| GET | `/feeds/google-merchant.xml` | 🌐 | Feed de produtos (SEO-14) |

Detalhes implementados na Fase 1:

- **`GET /products`**: `colors` são **slugs** de cor e `sizes` **nomes** de tamanho, repetindo o parâmetro (`colors=preto&colors=azul`). Um produto só entra quando **uma mesma variação ativa** atende à cor e ao tamanho pedidos. `sort` ∈ `createdAt:desc` (padrão), `createdAt:asc`, `price:asc`, `price:desc`, `name:asc`, `name:desc`; o preço usado em filtro e ordenação é o "a partir de". Padrões: `page=1`, `pageSize=20`. Entrada inválida (`pageSize` fora de 1–100, `page` < 1, `maxPrice` < `minPrice`, `sort` fora da lista, `search` > 100 caracteres, mais de 20 cores/tamanhos) → `400` com `code: "Validation.Failed"` e `errors` por campo. Página além do fim → `items: []` com o `totalItems` real.
- Item da listagem: `{ id, slug, name, categorySlug, priceFrom, colors: [{ id, name, slug, hex }], image: { url, alt, colorId } | null }`.
- **`GET /products/{slug}`**: slug antigo → `301` com `Location: /api/v1/catalog/products/{slugAtual}`; produto inativo, sem variação ativa ou em categoria inativa → `410` (`code: "Catalog.ProductUnavailable"`, extensão `categorySlug`, nula se a categoria também saiu); slug desconhecido → `404` (`Catalog.ProductNotFound`). As variações trazem `gtin` (código de barras, para o JSON-LD) e `available`, nunca a quantidade.
- **`GET /filters`** → `{ colors, sizes, minPrice, maxPrice }` considerando só variações visíveis na loja.
- **`GET /sitemap`** → `{ categories: [{ slug, updatedAt }], products: [{ slug, updatedAt }] }`.
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
| POST | `/quotes` | 🌐 | `{ postalCode, items: [{ variantId, quantity }] }` → opções (RF-032) |
| GET | `/postal-codes/{cep}` | 🌐 | Endereço pelo CEP (proxy ViaCEP com cache) |

Resposta:
```json
{ "options": [
  { "serviceCode": "PAC",   "name": "PAC",   "price": 0.00,  "originalPrice": 21.40, "deliveryDays": 7, "appliedRule": "Frete grátis acima de R$ 299" },
  { "serviceCode": "SEDEX", "name": "SEDEX", "price": 38.90, "originalPrice": 38.90, "deliveryDays": 3 }
] }
```

## 6. Carrinho — `/api/v1/cart`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| GET | `/` | 👤 | Carrinho com preços atuais e alertas de estoque |
| POST | `/items` | 👤 | `{ variantId, quantity }` |
| PUT | `/items/{variantId}` | 👤 | `{ quantity }` |
| DELETE | `/items/{variantId}` | 👤 | Remove item |
| DELETE | `/` | 👤 | Limpa |
| POST | `/merge` | 👤 | `{ items: [{ variantId, quantity }] }` (RF-031) |

## 7. Pedidos e pagamentos (cliente) — `/api/v1/orders`

| Método | Rota | Acesso | Descrição |
|--------|------|--------|-----------|
| POST | `/` | 👤 | Checkout `{ addressId, shippingServiceCode, notes? }` + `Idempotency-Key` (RF-040) → 201 |
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
| GET/POST | `/colors`, `/sizes` | Cores e tamanhos (RF-057) |
| PUT/DELETE | `/colors/{id}`, `/sizes/{id}` | Editar/remover (se não usado) |
| GET/POST | `/products` | Listar / criar (RF-051) |
| GET/PUT | `/products/{id}` | Detalhe / editar |
| PATCH | `/products/{id}/status` | Ativar/desativar |
| POST | `/products/{id}/variants/generate` | `{ colorIds[], sizeIds[] }` gera a grade (RF-056) |
| PUT | `/products/{id}/variants/{variantId}` | Editar variação |
| PATCH | `/products/{id}/variants/{variantId}/status` | Ativar/desativar |
| POST | `/products/{id}/images` | Upload de imagem (multipart) *(aprovado — arquivo 20, D06: Cloudflare R2)* |
| PUT/DELETE | `/products/{id}/images/{imageId}` | Ordem, alt, cor / remover |
| GET | `/variants/by-barcode/{code}` | Localiza a variação pelo código de barras (RF-059) |
| POST | `/stock/scan` | `{ barcode, quantity = 1, reason }` entrada de estoque por leitura |
| GET | `/stock?lowStock=true&search=` | Estoque por variação (busca também por código de barras) |
| POST | `/stock/{variantId}/movements` | `{ type, quantity, reason }` (RF-052) |
| GET/PUT | `/shipping/settings` | CEP de origem, manuseio, contingência (RF-058) |
| GET/POST | `/shipping/rules` | Regras de frete |
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
