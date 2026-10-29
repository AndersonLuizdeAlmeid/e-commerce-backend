# Entrega — Fase 1: Vitrine (catálogo com variações e SEO)

**Status:** Implementada em 20/10/2026, compilando. **Validação em andamento.**
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend`.

## 1. Onde estamos

| Fase | Situação |
|------|----------|
| 0 — Esqueleto ponta a ponta | ✅ Concluída (19/10/2026): solution, Docker, CI, categorias (RF-022), home e página de categoria |
| **1 — Vitrine** | 🔎 **Esta entrega** — código pronto, falta executar e aceitar |
| 2 — Identidade, e-mails e admin de catálogo | Próxima. Depende das decisões D04 (provedor de e-mail) e D06 (upload de fotos) |
| 3 a 6 | Sem mudança no planejamento (arquivo 19) |

O que faltava para a Fase 1, segundo as specs: produtos, grade cor × tamanho, imagens, filtros (RF-020, 021, 023), redirects e 410 (SEO-02), JSON-LD, sitemap e robots (SEO-05/06), cache HybridCache e HTTP (arquivo 12) e as telas da vitrine (arquivo 16 §4).

## 2. Objetivos

1. O visitante **navega pelo catálogo real**: home com novidades, categoria com filtros de cor, tamanho e preço, ordenação e paginação, busca e página de produto.
2. Na página do produto ele escolhe **cor (bolinha + nome) e tamanho**, vê preço e disponibilidade da combinação e a galeria muda com a cor, sem ver a quantidade em estoque.
3. As páginas públicas chegam **prontas para o Google**: HTML do servidor, title/description/canonical, JSON-LD de produto, trilha e loja, sitemap, robots, redirects permanentes e 404 real.
4. O catálogo é servido com **cache** (HybridCache na API, `Cache-Control`/`ETag` no HTTP, ISR/fetch cache no Next.js).
5. O banco da fase pode ser aplicado **à mão pelo DBeaver**, sem rodar o migrator.

## 3. Escopo entregue

### Banco (`src/ECommerce.Infrastructure/Persistence`)

| Script | Conteúdo |
|--------|----------|
| `V0003__catalog_products_and_variants.sql` | `colors`, `sizes`, `products`, `product_variants` (grade única por cor+tamanho, SKU e código de barras únicos), `product_images` (galeria por cor), `slug_history` |
| `V0004__inventory_stock_items.sql` | schema `inventory` + `stock_items` (só o saldo; antecipado da Fase 3) |
| `V0005__seed_colors_and_sizes.sql` | 7 cores básicas e 14 tamanhos (PP–XG, 34–46, Único), em todos os ambientes |
| `Seeds/Development/S0002__example_products.sql` | 9 produtos de exemplo (1 inativo), grades, preços por variação, EAN-13 válidos, estoque com combinações esgotadas, 2 fotos por cor, 1 slug antigo |

Para o DBeaver: `database/fase-1-schema.sql` e `database/fase-1-dados-exemplo.sql` (ver `database/README.md`).

### API (`/api/v1/catalog`)

| Endpoint | Requisito |
|----------|-----------|
| `GET /products` — paginação, filtros `category`, `search`, `colors[]`, `sizes[]`, `minPrice`, `maxPrice`, `sort` | RF-020 CA1–CA4 |
| `GET /products/{slug}` — variações, grade, imagens, `available`, `gtin`; 301 slug antigo; 410 inativo; 404 | RF-021 CA1–CA2, SEO-02 |
| `GET /filters?category=` — cores, tamanhos e faixa de preço | RF-023 |
| `GET /sitemap` | SEO-06 |
| `GET /categories`, `GET /categories/{slug}` — agora com cache | RF-022, arquivo 12 |

Peças transversais que entraram nesta fase porque ela é a primeira a precisar delas: **FluentValidation** com decorator de validação de queries (400 com `errors` por campo), **`ICacheService`/HybridCache**, filtro **`WithPublicHttpCache()`** (`Cache-Control` + `ETag` + `304`), `PagedResponse<T>`, números estritos no JSON (o OpenAPI deixa de publicar `number | string`).

### Frontend

| Página | Renderização | SEO |
|--------|--------------|-----|
| `/` home: categorias + 8 novidades | ISR 5 min | Organization + WebSite/SearchAction (no layout) |
| `/categoria/[slug]` com filtros, ordenação e `?pagina=N` | SSR com dados em cache | canonical por página, `noindex,follow` com filtros, BreadcrumbList |
| `/produto/[slug]` galeria por cor, seletor de cor/tamanho, `?cor=&tamanho=` | ISR 10 min | title/description/canonical, Open Graph com preço, ProductGroup/Offer/GTIN, BreadcrumbList, redirect permanente para slug novo ou para a categoria |
| `/busca?q=` | SSR | `noindex,follow` |
| `/sitemap.xml`, `/robots.txt` | revalida 1 h | SEO-06 |

Também: `next/image` com AVIF/WebP, 404 com busca, Lighthouse CI no workflow (SEO ≥ 95 e acessibilidade ≥ 90 bloqueiam; performance ≥ 90 é aviso) e stub da API do CI ampliado.

### Testes escritos (ainda não executados)

| Projeto | O que cobre |
|---------|-------------|
| `ECommerce.Application.UnitTests` (novo) | validator da listagem (faixas, whitelist de ordenação, limites), SEO padrão |
| `ECommerce.Api.FunctionalTests` | RF-020 CA1–CA4, filtro cor+tamanho na mesma variação, ordenação, paginação além do fim, 400 por campo; RF-021 CA1–CA2, 301, 410, 404; RF-023; sitemap; cache CA1 (segunda leitura não vai ao banco), disponibilidade fresca com produto em cache, `ETag`/`304`, erros sem cache público |
| `ECommerce.ArchitectureTests` | + Application não referencia HybridCache, decorators selados e internos, validators selados |
| Frontend (Vitest) | grade de variações, JSON-LD, parâmetros da listagem e paginação, cliente da API (301/410/404 e query string), formatação de preço |

## 4. Fora do escopo desta entrega

- Escrita no catálogo (admin), invalidação de cache após escrita e revalidação sob demanda das páginas → Fase 2.
- Upload de fotos e CDN (D06). As fotos de exemplo são ilustrativas (placehold.co).
- Carrinho, frete e compra: o botão "Adicionar ao carrinho" aparece desabilitado → Fase 3.
- Redirect 301 de categoria, feed do Merchant Center → Fases 2 e 6.

## 5. Como validar

A validação foi pensada em três etapas: só build primeiro, depois banco e, por fim, execução.

### Etapa A — build, sem executar nada

| # | Comando | Esperado | Situação |
|---|---------|----------|----------|
| A1 | `dotnet build ECommerce.sln` (backend) | 0 erros, 0 avisos (`TreatWarningsAsErrors`) | ✅ verificado em 20/10/2026 |
| A2 | `dotnet format ECommerce.sln --verify-no-changes` | sem alterações | ✅ verificado em 20/10/2026 |
| A3 | `npm ci && npm run lint && npm run typecheck` (frontend) | sem erros | ✅ verificado em 26/10/2026 |
| A4 | `npm test` (frontend) | todos verdes (não sobe servidor) | ✅ 27 testes, verificado em 26/10/2026 |
| A5 | `node scripts/ci-api-stub.mjs` em um terminal e `npm run build` em outro, com `API_INTERNAL_URL=http://localhost:5099` | build conclui; rotas `/` e `/sitemap.xml` estáticas, `/produto/[slug]` ISR, `/categoria/[slug]` e `/busca` dinâmicas | ✅ verificado em 26/10/2026 |

### Etapa B — banco pelo DBeaver

| # | Passo | Esperado |
|---|-------|----------|
| B1 | Rodar `database/fase-1-schema.sql` no banco que já tem a Fase 0 | `COMMIT` sem erro; `public.schemaversions` com V0003, V0004 e V0005 |
| B2 | Rodar `database/fase-1-dados-exemplo.sql` (só dev) | 9 produtos; `camiseta-basica` com 24 variações |
| B3 | Rodar B1 de novo | falha logo no início com "Script já aplicado", sem alterar nada |
| B4 | `docker compose up -d postgres migrator` depois do B1/B2 | migrator termina sem reaplicar nada |

### Etapa C — execução

| # | Verificação | Esperado |
|---|-------------|----------|
| C1 | `dotnet test ECommerce.sln` (Docker ligado) | todos verdes |
| C2 | `GET /health/ready` | `Healthy` (sem migration pendente depois do DBeaver) |
| C3 | Scalar em `/docs` | 6 endpoints do catálogo documentados |
| C4 | `npm run gen:api` e `git diff src/shared/api/schema.d.ts` | sem diferença relevante (o arquivo foi escrito à mão nesta entrega, seguindo o formato do gerador) |
| C5 | `npm run dev` e navegar | home com novidades; categoria filtra por cor/tamanho/preço e pagina; produto troca galeria e preço por cor/tamanho; tamanho inexistente desabilitado e esgotado riscado |
| C6 | `/produto/camiseta-basica-algodao` | redirect permanente para `/produto/camiseta-basica` |
| C7 | `/produto/camiseta-colecao-2024` | redirect permanente para `/categoria/camisetas` |
| C8 | `/produto/nao-existe` | página 404 com status HTTP 404 |
| C9 | Ver código-fonte da página de produto | JSON-LD `ProductGroup` com `gtin` nas variações da camiseta básica; canonical sem `?cor=` |
| C10 | `/categoria/camisetas?cor=preto` | `<meta name="robots" content="noindex, follow">` |
| C11 | `/sitemap.xml` e `/robots.txt` | só itens ativos; `/busca`, `/conta`, `/checkout`, `/admin` bloqueados |
| C12 | `curl -i` duas vezes em `/api/v1/catalog/products`, a segunda com `If-None-Match` | `200` com `ETag`, depois `304` |
| C13 | Lighthouse (local ou no CI) em home, categoria e produto | SEO ≥ 95, acessibilidade ≥ 90 |

### Critério de aceite da entrega

A entrega é aceita quando **A1–A5, B1–B4 e C1–C13** passarem e o cliente aprovar a navegação da vitrine (C5) em staging. Falhas em C13 de **performance** não bloqueiam (as imagens ainda são ilustrativas), mas viram tarefa da Fase 2 junto com o CDN.

## 6. Riscos e pontos de atenção

| Risco | Mitigação |
|-------|-----------|
| Frontend não foi compilado na implementação (sem Node.js) | A3–A5 são os primeiros passos da validação; erros de tipo devem ser pequenos e localizados |
| `schema.d.ts` escrito à mão | C4 regenera e compara |
| Página de categoria ficou dinâmica (lê filtros da URL) e não ISR | Os dados continuam cacheados (fetch + HybridCache); se o TTFB pesar, mover paginação/filtros para segmentos de rota |
| Cache de produto inexistente por 10 min (um produto criado logo depois de um 404 demora a aparecer) | Some com a invalidação por tag do admin (Fase 2) |
| `next start` com `output: standalone` imprime aviso no Lighthouse CI | Inofensivo; no deploy usa-se `node .next/standalone/server.js` |

## 7. Decisões tomadas nesta entrega

- Produto inativo: API responde **410**, a página redireciona permanentemente para a categoria (D11, a confirmar com o cliente).
- Filtros da URL em português: `?cor=`, `?tamanho=`, `?preco_min=`, `?preco_max=`, `?ordem=`, `?pagina=`.
- Filtro por cor + tamanho exige **a mesma variação** (azul **e** M), não "tem azul e tem M em variações diferentes".
- `inventory.stock_items` antecipada da Fase 3; só leitura nesta fase.

## 8. Próxima entrega — Fase 2 (prévia)

**Objetivo:** cliente cria conta e entra; admin cadastra e edita o catálogo que a Fase 1 exibe.

Antes de começar, precisamos do cliente: **D04** (provedor e domínio de e-mail), **D06** (upload de fotos e CDN), **D05** (exigir e-mail confirmado), **D10** (uso do código de barras) e **D11/D12** desta entrega.

Escopo previsto: `identity.users`, `refresh_tokens`, `user_tokens` e `customers`; RF-001..009 e RF-060; JWT + refresh em cookie; rate limiter; `IEmailSender` com Mailpit; agregados `Product`/`Category`/`Color`/`Size` no Domain (VOs `Slug`, `Sku`, `Money`, `Gtin`, `HexColor`, `SeoMetadata`); `IUnitOfWork` e decorator transacional; RF-050/051/056/057/059 com invalidação de cache e revalidação sob demanda; telas de login, cadastro, recuperação de senha e admin de catálogo.
